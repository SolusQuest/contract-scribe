using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;

namespace ContractScribe.Tests;

public sealed class M1HostValidationPublicationFailureTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Join(
        Root,
        "tests",
        "fixtures",
        "m1-host-validation",
        "v1");
    private static readonly string SubjectSchema = Path.Join(
        Root,
        "schemas",
        "validation",
        "m1-host-validation-subject-v1.schema.json");
    private static readonly string CellEvidenceSchema = Path.Join(
        Root,
        "schemas",
        "validation",
        "m1-host-validation-cell-evidence-v1.schema.json");

    [Fact]
    public void PublicationFailure_ClosedDomainsAndFrozenVectorsAreExact()
    {
        var context = BundleValidator.Validate(Root);
        Assert.Contains("publication-failure", context.Protocol.Taxonomies.ExecutionOutcome);

        var invalidation = context.Vectors.Vectors.Single(vector =>
            vector.VectorId == "failure.publication-invalidation");
        AssertVector(
            invalidation,
            "publication.invalidation-fault",
            "publication.invalidation-failure-committed",
            ["artifact-state", "filesystem-delta", "known-prestate", "selected-toolchain", "subject-response"],
            ["fixture", "host-source", "protocol"]);

        var finalization = context.Vectors.Vectors.Single(vector =>
            vector.VectorId == "failure.publication-finalization");
        AssertVector(
            finalization,
            "publication.atomic-replace-fault",
            "publication.finalization-failure-committed",
            ["artifact-state", "filesystem-delta", "selected-toolchain", "staged-canonical-bytes", "subject-response"],
            ["fixture", "host-source", "protocol", "toolchain"]);

        Assert.Null(RunSemantics.ExpectedControl(invalidation.VectorId));
        Assert.Equal(
            ("publication-staging-ready", "observe"),
            RunSemantics.ExpectedControl(finalization.VectorId));
        Assert.True(RunSemantics.HasExactTransitionTrace(
            invalidation.VectorId,
            InvalidationTransitions));
        Assert.True(RunSemantics.HasExactTransitionTrace(
            finalization.VectorId,
            FinalizationTransitions));

        var template = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
            Path.Join(FixtureRoot, "execution-subject.template.json"),
            4 * 1024 * 1024);
        Assert.Equal(2, template.Cells.Count);
        foreach (var cell in template.Cells)
        {
            var invalidationFixture = cell.Fixtures.Single(item =>
                item.VectorId == invalidation.VectorId);
            Assert.Equal("prior-valid", invalidationFixture.ResultPrestate);
            Assert.Equal("TestResults/audit-result.json", invalidationFixture.ResultPath);

            var finalizationFixture = cell.Fixtures.Single(item =>
                item.VectorId == finalization.VectorId);
            Assert.Equal("absent", finalizationFixture.ResultPrestate);
            Assert.Equal("TestResults/audit-result.json", finalizationFixture.ResultPath);
        }

        SchemaValidation.Validate(
            Path.Join(FixtureRoot, "self-test-host-failure-registry.json"),
            Path.Join(
                Root,
                "schemas",
                "validation",
                "m1-host-failure-registry-v1.schema.json"),
            requireCanonical: true);
    }

    [Fact]
    public void PublicationFailure_LegalFormsPassStandaloneCellAndSemanticOracles()
    {
        var context = BundleValidator.Validate(Root);
        foreach (var finalization in new[] { false, true })
        {
            var @case = BuildCase(context, finalization);
            var derived = Derive(@case);
            Assert.Equal("matched", derived.Verdict);
            Assert.Equal(@case.Vector.ExpectedObservation, derived.Observation);
            Assert.Equal("internally-enforceable", derived.EnforcementClass);
            Assert.Empty(derived.DiagnosticCodes);

            ValidateDefinition(@case.Request, SubjectSchema, "subjectRequest");
            ValidateDefinition(@case.Run.Subject!, SubjectSchema, "subjectResponse");
            ValidateRoot(@case.Evidence, CellEvidenceSchema);
        }
    }

    [Fact]
    public void PublicationFailure_RequestAndHostFactSchemasRejectClosedShapeMutations()
    {
        var context = BundleValidator.Validate(Root);
        var invalidation = BuildCase(context, finalization: false);
        var finalization = BuildCase(context, finalization: true);

        AssertDefinitionRejected(
            finalization.Request,
            SubjectSchema,
            "subjectRequest",
            root => root["publicationFault"]!["operation"] = "invalidate-existing");
        AssertDefinitionRejected(
            invalidation.Request,
            SubjectSchema,
            "subjectRequest",
            root => root["postTerminalAttempt"]!["executionOutcome"] = "audit-error");
        AssertDefinitionRejected(
            invalidation.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root => root["executionOutcome"] = "future-publication-outcome");
        AssertDefinitionRejected(
            finalization.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root => root["executionOutcome"] = "audit-error");
        AssertDefinitionRejected(
            invalidation.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root =>
            {
                root["vectorId"] = "failure.invalid-input";
                root["executionOutcome"] = "invalid-input";
                root["failureCode"] = "host.invalid-input";
                root["failureStage"] = "input";
                var facts = root["hostFacts"]!.AsObject();
                facts["toolchainSelectionState"] = "selected";
                facts["selectedSdk"] = "10.0.102";
                facts["selectedRuntime"] = "10.0.0";
                facts["selectedMsbuild"] = "18.0.0";
            });
        AssertDefinitionRejected(
            invalidation.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root =>
            {
                root["vectorId"] = "contracts.outcome-compliant";
                root["auditOutcome"] = "compliant";
                root["executionOutcome"] = "succeeded";
                root["failureRegistryIdentity"] = null;
                root["failureCode"] = null;
                root["failureStage"] = null;
            });
        AssertDefinitionRejected(
            finalization.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root => root["hostFacts"]!.AsObject().Remove("selectedRuntime"));
        AssertDefinitionRejected(
            finalization.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root =>
            {
                var facts = root["hostFacts"]!.AsObject();
                facts.Remove("selectedRuntime");
                facts.Remove("selectedMsbuild");
            });
        AssertDefinitionRejected(
            invalidation.Run.Subject!,
            SubjectSchema,
            "subjectResponse",
            root => root["hostFacts"]!["selectedSdk"] = "10.0.102");

        AssertRootRejected(
            finalization.Evidence,
            CellEvidenceSchema,
            root => root["runs"]![0]!["subject"]!["hostFacts"]!
                .AsObject().Remove("selectedRuntime"));
        AssertRootRejected(
            finalization.Evidence,
            CellEvidenceSchema,
            root => root["runs"]![0]!["subject"]!["executionOutcome"] =
                "audit-error");
        AssertRootRejected(
            invalidation.Evidence,
            CellEvidenceSchema,
            root => root["runs"]![0]!["subject"]!["hostFacts"]!["selectedSdk"] =
                "10.0.102");
        AssertRootRejected(
            finalization.Evidence,
            CellEvidenceSchema,
            root => root["runs"]![0]!["publicationArtifactObservation"]!
                .AsObject().Remove("stagingDisposition"));
    }

    [Fact]
    public void PublicationFailure_MutationCorpusIsClosedAndExecutable()
    {
        var context = BundleValidator.Validate(Root);
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "evidence-mutation-corpus.json")));
        var cases = document.RootElement.GetProperty("publicationFailureCases")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(17, cases.Length);
        Assert.Equal(
            cases.Length,
            cases.Select(item => item.GetProperty("caseId").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var item in cases)
        {
            var seed = item.GetProperty("seed").GetString();
            var operation = item.GetProperty("operation").GetString()
                ?? throw new InvalidOperationException("Mutation operation is required.");
            var expectedCode = item.GetProperty("expectedCode").GetString();
            var failure = Assert.Throws<ProtocolException>(() =>
                ExecuteMutationCase(context, seed, operation));
            Assert.Equal(expectedCode, failure.Code);
        }
    }

    [Fact]
    public void PublicationFailure_DirectoryLifecycleRejectsStaleAndRenamedStaging()
    {
        var repository = TemporaryPublicationRepository();
        var result = Path.Join(repository, "TestResults", "audit-result.json");
        var staging = Path.Join(
            repository,
            "TestResults",
            ".audit-result.json.contractscribe-stage");
        try
        {
            WriteCanonicalAuditResult(result);
            WriteCanonicalAuditResult(staging);
            AssertProtocol(
                "HV253_PUBLICATION_DIRECTORY_STATE",
                () => CellExecutor.RequireClosedPublicationDirectory(
                    repository,
                    expectResult: true,
                    expectStaging: false));

            File.Delete(staging);
            CellExecutor.RequireClosedPublicationDirectory(
                repository,
                expectResult: true,
                expectStaging: false);

            File.Delete(result);
            WriteCanonicalAuditResult(staging);
            File.Move(
                staging,
                Path.Join(repository, "TestResults", ".renamed-publication-residual"));
            AssertProtocol(
                "HV253_PUBLICATION_DIRECTORY_STATE",
                () => CellExecutor.RequireClosedPublicationDirectory(
                    repository,
                    expectResult: false,
                    expectStaging: false));

            File.Delete(Path.Join(
                repository,
                "TestResults",
                ".renamed-publication-residual"));
            WriteCanonicalAuditResult(staging);
            CellExecutor.ResetPublicationDirectoryForProvisioning(repository);
            CellExecutor.RequireClosedPublicationDirectory(
                repository,
                expectResult: false,
                expectStaging: false);
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task PublicationFailure_StagingObservationRejectsDirectoryAndOversizedFile()
    {
        var repository = TemporaryPublicationRepository();
        var staging = Path.Join(
            repository,
            "TestResults",
            ".audit-result.json.contractscribe-stage");
        try
        {
            Directory.CreateDirectory(staging);
            CellExecutor.RequireClosedPublicationDirectory(
                repository,
                expectResult: false,
                expectStaging: true);
            await AssertProtocolAsync(
                "HV254_PUBLICATION_ARTIFACT_UNSAFE",
                () => CellExecutor.ObserveCanonicalResultAsync(
                    Root,
                    repository,
                    FrozenFixtureRegistry.StagingPath,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));

            Directory.Delete(staging);
            await File.WriteAllBytesAsync(staging, new byte[(4 * 1024 * 1024) + 1]);
            await AssertProtocolAsync(
                "HV102_ARTIFACT_SIZE",
                () => CellExecutor.ObserveCanonicalResultAsync(
                    Root,
                    repository,
                    FrozenFixtureRegistry.StagingPath,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public async Task PublicationFailure_StagingObservationRejectsLinksAndUnixSpecialFiles()
    {
        var repository = TemporaryPublicationRepository();
        var source = Path.Join(repository, ".canonical-source.json");
        var staging = Path.Join(
            repository,
            "TestResults",
            ".audit-result.json.contractscribe-stage");
        try
        {
            WriteCanonicalAuditResult(source);
            try
            {
                _ = File.CreateSymbolicLink(staging, source);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
            {
                return;
            }
            AssertProtocol(
                "HV188_FIXTURE_PATH_INVALID",
                () => CellExecutor.RequireClosedPublicationDirectory(
                    repository,
                    expectResult: false,
                    expectStaging: true));
            File.Delete(staging);

            _ = File.CreateSymbolicLink(staging, Path.Join(repository, ".missing-source.json"));
            AssertProtocol(
                "HV188_FIXTURE_PATH_INVALID",
                () => CellExecutor.RequireClosedPublicationDirectory(
                    repository,
                    expectResult: false,
                    expectStaging: true));
            File.Delete(staging);

            if (!OperatingSystem.IsLinux()) return;
            Assert.Equal(0, MkFifo(staging, Convert.ToUInt32("600", 8)));
            CellExecutor.RequireClosedPublicationDirectory(
                repository,
                expectResult: false,
                expectStaging: true);
            var stopwatch = Stopwatch.StartNew();
            await AssertProtocolAsync(
                "HV254_PUBLICATION_ARTIFACT_UNSAFE",
                () => CellExecutor.ObserveCanonicalResultAsync(
                    Root,
                    repository,
                    FrozenFixtureRegistry.StagingPath,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            DeleteTemporaryRepository(repository);
        }
    }

    [Fact]
    public void PublicationFailure_RegistryRejectsNonPublicationStageOneWay()
    {
        var registryPath = Path.Join(FixtureRoot, "self-test-host-failure-registry.json");
        var schemaPath = Path.Join(
            Root,
            "schemas",
            "validation",
            "m1-host-failure-registry-v1.schema.json");
        var invalid = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
        invalid["entries"]!.AsArray().Add(new JsonObject
        {
            ["code"] = "host.test-only.invalid-publication-stage",
            ["stage"] = "audit",
            ["executionOutcome"] = "publication-failure",
            ["terminalState"] = "committed-non-success"
        });
        var invalidPath = TemporaryJsonPath();
        try
        {
            CanonicalJson.WriteCanonical(invalidPath, invalid);
            AssertProtocol(
                "HV111_SCHEMA_REJECTED",
                () => SchemaValidation.Validate(
                    invalidPath,
                    schemaPath,
                    requireCanonical: true));
            using var invalidDocument = CanonicalJson.ReadStrict(
                invalidPath,
                1024 * 1024,
                requireCanonical: true);
            AssertProtocol(
                "HV228_HOST_REGISTRY_INVALID",
                () => CellExecutor.ValidateHostFailureRegistrySemantics(
                    invalidDocument.RootElement));

            var legal = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
            legal["entries"]!.AsArray().Add(new JsonObject
            {
                ["code"] = "host.test-only.cancelled-during-publication",
                ["stage"] = "publication",
                ["executionOutcome"] = "cancelled",
                ["terminalState"] = "committed-non-success"
            });
            CanonicalJson.WriteCanonical(invalidPath, legal);
            SchemaValidation.Validate(invalidPath, schemaPath, requireCanonical: true);
            using var legalDocument = CanonicalJson.ReadStrict(
                invalidPath,
                1024 * 1024,
                requireCanonical: true);
            CellExecutor.ValidateHostFailureRegistrySemantics(legalDocument.RootElement);
        }
        finally
        {
            File.Delete(invalidPath);
        }
    }

    private static readonly string[] InvalidationTransitions =
    [
        "invalidation-attempt-failed",
        "terminal-commit-publication-failure",
        "late-terminal-attempt-rejected"
    ];

    private static readonly string[] FinalizationTransitions =
    [
        "invalidation-completed",
        "failure-prone-stage-entered",
        "staging-created-in-destination",
        "atomic-replace-attempt-failed",
        "staging-cleanup-completed",
        "terminal-commit-publication-failure",
        "late-terminal-attempt-rejected"
    ];

    private static void AssertVector(
        VectorDefinition vector,
        string fixture,
        string observation,
        string[] observers,
        string[] protectedInputs)
    {
        Assert.Equal("failure", vector.Category);
        Assert.Equal("production-host", vector.ExecutorKind);
        Assert.Equal(["ubuntu-x64", "windows-x64"], vector.Cells);
        Assert.Equal(1, vector.InvocationCount);
        Assert.True(vector.FreshProcessPerInvocation);
        Assert.Equal(["run-1"], vector.RunIds);
        Assert.Equal(
            [
                "subject.executionOutcome",
                "subject.failureStage",
                "subject.terminalState",
                "subject.artifactState",
                "subject.hostFacts.toolchainSelectionState"
            ],
            vector.EqualityFields);
        Assert.True(vector.CrossCellEquality);
        Assert.Equal(fixture, vector.Fixture);
        Assert.Equal(observation, vector.ExpectedObservation);
        Assert.Equal("internally-enforceable", vector.ExpectedEnforcementClass);
        Assert.Equal("required", vector.SupportDisposition);
        Assert.Equal(observers, vector.ObserverRequirements);
        Assert.Equal(protectedInputs, vector.ProtectedInputClasses);
    }

    private static PublicationCase BuildCase(BundleContext context, bool finalization)
    {
        const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";
        const string HostRevision = "2222222222222222222222222222222222222222";
        var vectorId = finalization
            ? "failure.publication-finalization"
            : "failure.publication-invalidation";
        var vector = context.Vectors.Vectors.Single(item => item.VectorId == vectorId);
        var registryPath = "tests/fixtures/m1-host-validation/v1/self-test-host-failure-registry.json";
        var registrySha = CanonicalJson.Sha256File(Path.Join(Root, registryPath));
        var source = new SubjectSourceConfiguration(
            $"source.{Sha}",
            HostRevision,
            $"operations.{Sha}",
            ["src"],
            [new ArtifactIdentity("src/ContractScribe.Core/ContractScribe.Core.csproj", Sha)],
            new ArtifactIdentity(registryPath, registrySha),
            new ArtifactIdentity("src/ContractScribe.Core/Hosting/host-calibrated-bounds-v1.json", Sha),
            new ArtifactIdentity("Directory.Build.props", Sha),
            new ArtifactIdentity("schemas/validation/m1-host-validation-subject-v1.schema.json", Sha),
            new ArtifactIdentity("tests/fixtures/m1-contract-baseline/v1/manifest.json", Sha),
            new ArtifactIdentity("docs/20_architecture/security-boundary.md", Sha),
            new ArtifactIdentity(".github/workflows/ci.yml", Sha));
        var materialization = new CellMaterialization(
            "ubuntu-x64",
            "1",
            "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
            "ubuntu-24.04",
            "linux-x64",
            "X64",
            "10.0.102",
            "10.0.0",
            "18.0.0",
            [
                new ArtifactIdentity("src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll", Sha),
                new ArtifactIdentity("src/ContractScribe.Core/bin/Release/net10.0/ContractScribe.Core.dll", Sha)
            ]);
        var fixture = new FixtureRealization(
            vectorId,
            "production-host",
            $"tests/fixtures/m1-host-validation/runtime/ubuntu-x64/{vectorId}",
            Sha,
            true,
            null,
            "dotnet",
            [],
            Sha,
            [],
            ["obj"],
            "direct-process",
            "TestResults/audit-result.json",
            finalization ? "absent" : "prior-valid",
            [new RunWorkingDirectory("run-1", "repository-root")],
            null);

        var commitment = new CanonicalResultCommitment(
            Sha,
            256,
            "canonical-json-utf8-no-bom-single-lf",
            true);
        var facts = new HostObservationFacts(
            source.SourceConfigurationId,
            source.HostRevision,
            source.ContractBaseline.Sha256,
            source.FailureRegistry.Sha256,
            source.CalibratedBounds.Sha256,
            finalization ? materialization.SelectedSdk : null,
            finalization ? materialization.SelectedRuntime : null,
            finalization ? materialization.SelectedMsbuild : null,
            [new NormalizedDiagnosticFact(
                finalization
                    ? "host.test-only.publication-finalization"
                    : "host.test-only.publication-invalidation",
                "publication")],
            new OutputCommitFact("not-committed", null),
            [],
            null,
            finalization ? "selected" : "not-selected");
        var subject = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vectorId,
            "run-1",
            "started",
            "normal",
            null,
            "publication-failure",
            source.FailureRegistry.Sha256,
            finalization
                ? "host.test-only.publication-finalization"
                : "host.test-only.publication-invalidation",
            "publication",
            "committed",
            "invalidated",
            "internally-enforceable",
            vector.ExpectedObservation,
            null,
            facts);
        var process = new ProcessObservation(
            1,
            "started",
            "normal",
            false,
            finalization,
            true,
            ObservedGateName: finalization ? "publication-staging-ready" : null,
            ObservedControlAction: finalization ? "observe" : null,
            PostGateSampleObserved: finalization,
            ObservedControlOutcome: finalization ? "observed" : null,
            TransitionEvents: finalization ? FinalizationTransitions : InvalidationTransitions);
        var publicationObservation = finalization
            ? new PublicationArtifactObservation(null, null, "absent", commitment, "cleaned")
            : new PublicationArtifactObservation(
                commitment,
                commitment,
                "pre-existing",
                null,
                "not-created");
        var run = new RunEvidence(
            vectorId,
            "run-1",
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            subject,
            process,
            null,
            null,
            EmptyDelta(),
            [],
            [],
            publicationObservation);
        var request = new SubjectRequest(
            "contractscribe-m1-host-validation-subject-request-v1",
            "self-test",
            vectorId,
            "run-1",
            Root,
            Path.Join(Root, "TestResults", "self-test-response.json"),
            finalization ? Path.Join(Root, "TestResults", "self-test-control") : null,
            finalization ? ["publication-staging-ready"] : [],
            finalization ? "observe" : "continue",
            null,
            Path.Join(Root, "TestResults", "self-test-transition.json"),
            null,
            null,
            finalization
                ? new PublicationFault(
                    "atomic-replace",
                    1,
                    "io-exception",
                    "TestResults/.audit-result.json.contractscribe-stage")
                : new PublicationFault("invalidate-existing", 1, "io-exception", null),
            new PostTerminalAttempt(
                "succeeded",
                "after-publication-failure-commit",
                1));
        var evidence = new CellEvidence(
            "contractscribe-m1-host-validation-cell-evidence-v1",
            context.Lock.BundleId,
            context.Protocol.PublicSafety.NetworkClaimSetId,
            $"review.{Sha}",
            source.SourceConfigurationId,
            Sha,
            new ValidationAttemptIdentity(
                ".github/workflows/ci.yml",
                Sha,
                "1",
                1,
                HostRevision,
                HostRevision),
            materialization,
            [run],
            "passed");
        return new(context, vector, source, materialization, fixture, request, run, evidence);
    }

    private static DerivedRun Derive(PublicationCase @case) =>
        RunSemantics.Derive(
            @case.Context,
            @case.Vector,
            @case.Run,
            @case.Fixture,
            @case.Source,
            @case.Materialization);

    private static void ExecuteMutationCase(
        BundleContext context,
        string? seed,
        string operation)
    {
        var @case = BuildCase(
            context,
            finalization: seed == "publication-finalization");
        var run = @case.Run;
        var subject = run.Subject!;
        var facts = subject.HostFacts!;
        var commitment = run.PublicationArtifactObservation!.StagedCanonical
            ?? run.PublicationArtifactObservation.PreRunCanonical!;

        run = operation switch
        {
            "replace-outcome-with-unknown" => run with
            {
                Subject = subject with { ExecutionOutcome = "future-publication-outcome" }
            },
            "replace-outcome-with-audit-error" => run with
            {
                Subject = subject with { ExecutionOutcome = "audit-error" }
            },
            "remove-publication-artifact-observation" => run with
            {
                PublicationArtifactObservation = null
            },
            "remove-selected-runtime" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with { SelectedRuntime = null }
                }
            },
            "retain-only-selected-sdk" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with
                    {
                        SelectedRuntime = null,
                        SelectedMsbuild = null
                    }
                }
            },
            "add-identities-to-not-selected" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with
                    {
                        SelectedSdk = @case.Materialization.SelectedSdk,
                        SelectedRuntime = @case.Materialization.SelectedRuntime,
                        SelectedMsbuild = @case.Materialization.SelectedMsbuild
                    }
                }
            },
            "mark-invalidation-selected" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with { ToolchainSelectionState = "selected" }
                }
            },
            "mark-finalization-not-selected" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with
                    {
                        SelectedSdk = null,
                        SelectedRuntime = null,
                        SelectedMsbuild = null,
                        ToolchainSelectionState = "not-selected"
                    }
                }
            },
            "attribute-prior-result-to-current-invocation" => run with
            {
                PublicationArtifactObservation = run.PublicationArtifactObservation with
                {
                    PostRunAttribution = "current-invocation"
                }
            },
            "report-current-canonical-result" => run with
            {
                Subject = subject with { CanonicalResult = commitment },
                ObservedCanonicalResult = commitment
            },
            "report-published-artifact" => run with
            {
                Subject = subject with { ArtifactState = "published" }
            },
            "report-committed-output" => run with
            {
                Subject = subject with
                {
                    HostFacts = facts with
                    {
                        OutputCommit = new OutputCommitFact("committed", commitment.Sha256)
                    }
                }
            },
            "leave-residual-staging" => run with
            {
                PublicationArtifactObservation = run.PublicationArtifactObservation with
                {
                    StagingDisposition = "residual"
                }
            },
            "report-staged-bytes-as-current-result" => run with
            {
                PublicationArtifactObservation = run.PublicationArtifactObservation with
                {
                    PostRunCanonical = commitment,
                    PostRunAttribution = "current-invocation"
                }
            },
            "replace-publication-registry-row" => run with
            {
                Subject = subject with { FailureCode = "host.invalid-input" }
            },
            "swap-finalization-transition-order" => run with
            {
                Process = run.Process with
                {
                    TransitionEvents =
                    [
                        "failure-prone-stage-entered",
                        "invalidation-completed",
                        "staging-created-in-destination",
                        "atomic-replace-attempt-failed",
                        "staging-cleanup-completed",
                        "terminal-commit-publication-failure",
                        "late-terminal-attempt-rejected"
                    ]
                }
            },
            "replace-committed-failure-with-late-success" => run with
            {
                Subject = subject with
                {
                    AuditOutcome = "compliant",
                    ExecutionOutcome = "succeeded",
                    FailureRegistryIdentity = null,
                    FailureCode = null,
                    FailureStage = null,
                    ArtifactState = "published",
                    CanonicalResult = commitment,
                    HostFacts = facts with
                    {
                        NormalizedDiagnosticFacts = [],
                        OutputCommit = new OutputCommitFact("committed", commitment.Sha256)
                    }
                },
                ObservedCanonicalResult = commitment,
                ObservedAuditResult = new ObservedAuditResultFacts(
                    1,
                    1,
                    1,
                    "profile.external-api",
                    ["audit.outcome.compliant"])
            },
            _ => throw new InvalidOperationException($"Unknown mutation operation: {operation}")
        };

        _ = RunSemantics.Derive(
            @case.Context,
            @case.Vector,
            run,
            @case.Fixture,
            @case.Source,
            @case.Materialization);
    }

    private static RepositoryDelta EmptyDelta() => new(
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    private static void ValidateDefinition<T>(
        T value,
        string schema,
        string definition)
    {
        var path = TemporaryJsonPath();
        try
        {
            CanonicalJson.WriteCanonical(path, value);
            SchemaValidation.ValidateDefinition(path, schema, definition, requireCanonical: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ValidateRoot<T>(T value, string schema)
    {
        var path = TemporaryJsonPath();
        try
        {
            CanonicalJson.WriteCanonical(path, value);
            SchemaValidation.Validate(path, schema, requireCanonical: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertDefinitionRejected<T>(
        T value,
        string schema,
        string definition,
        Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(CanonicalJson.SerializeCanonical(value))!.AsObject();
        mutate(node);
        var path = TemporaryJsonPath();
        try
        {
            CanonicalJson.WriteCanonical(path, node);
            var failure = Assert.Throws<ProtocolException>(() =>
                SchemaValidation.ValidateDefinition(
                    path,
                    schema,
                    definition,
                    requireCanonical: true));
            Assert.Equal("HV111_SCHEMA_REJECTED", failure.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertRootRejected<T>(
        T value,
        string schema,
        Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(CanonicalJson.SerializeCanonical(value))!.AsObject();
        mutate(node);
        var path = TemporaryJsonPath();
        try
        {
            CanonicalJson.WriteCanonical(path, node);
            var failure = Assert.Throws<ProtocolException>(() =>
                SchemaValidation.Validate(path, schema, requireCanonical: true));
            Assert.Equal("HV111_SCHEMA_REJECTED", failure.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertProtocol(string code, Action action)
    {
        var failure = Assert.Throws<ProtocolException>(action);
        Assert.Equal(code, failure.Code);
    }

    private static async Task AssertProtocolAsync<T>(
        string code,
        Func<Task<T>> action)
    {
        var failure = await Assert.ThrowsAsync<ProtocolException>(action);
        Assert.Equal(code, failure.Code);
    }

    private static string TemporaryPublicationRepository()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-publication-observer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(path, "TestResults"));
        return path;
    }

    private static void WriteCanonicalAuditResult(string path)
    {
        var source = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "unresolved-classification.json");
        using var document = CanonicalJson.ReadStrict(source, 4 * 1024 * 1024);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            AuditResultV1Canonicalizer.Canonicalize(document.RootElement));
    }

    private static void DeleteTemporaryRepository(string repository)
    {
        try
        {
            Directory.Delete(repository, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The test assertion is authoritative; cleanup remains best-effort.
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private static string TemporaryJsonPath() => Path.Join(
        Path.GetTempPath(),
        $"contractscribe-publication-failure-{Guid.NewGuid():N}.json");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record PublicationCase(
        BundleContext Context,
        VectorDefinition Vector,
        SubjectSourceConfiguration Source,
        CellMaterialization Materialization,
        FixtureRealization Fixture,
        SubjectRequest Request,
        RunEvidence Run,
        CellEvidence Evidence);
}
