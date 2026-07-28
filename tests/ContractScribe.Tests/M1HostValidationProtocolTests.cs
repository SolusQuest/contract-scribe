using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;

namespace ContractScribe.Tests;

public sealed class M1HostValidationProtocolTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Join(Root, "tests", "fixtures", "m1-host-validation", "v1");

    [Fact]
    public void HostValidation_BundleIsClosedAndCurrent()
    {
        var context = BundleValidator.Validate(Root);

        Assert.Equal("m1-host-validation-v1", context.Protocol.ProtocolId);
        Assert.Equal("bb4654edc180e2953dda6b89a29211b18778b78e", context.Protocol.Baseline.MergeCommit);
        Assert.Matches("^m1hvp1\\.[0-9a-f]{64}$", context.Lock.BundleId);
        Assert.Equal(
            context.Protocol.ArtifactInventory.Order(StringComparer.Ordinal),
            context.Lock.Entries.Select(entry => entry.Path));
        Assert.DoesNotContain(
            context.Lock.Entries,
            entry => File.ReadAllText(Path.Join(Root, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
                .Contains(context.Lock.BundleId, StringComparison.Ordinal));
    }

    [Fact]
    public void HostValidation_ExpectedExecutionTriplesAreExactAndDeterminismIsFresh()
    {
        var context = BundleValidator.Validate(Root);
        var runs = context.Vectors.ExpandExpectedRuns().ToArray();
        var keys = runs.Select(run => $"{run.CellId}\0{run.VectorId}\0{run.RunId}").ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(runs, run => run is { CellId: "ubuntu-x64", VectorId: "path.symlink-escape", RunId: "run-1" });
        Assert.DoesNotContain(runs, run => run is { CellId: "windows-x64", VectorId: "path.symlink-escape" });
        Assert.Contains(runs, run => run is { CellId: "windows-x64", VectorId: "path.junction-reparse-escape", RunId: "run-1" });
        Assert.DoesNotContain(runs, run => run is { CellId: "ubuntu-x64", VectorId: "path.junction-reparse-escape" });

        var determinism = context.Vectors.Vectors.Single(vector => vector.VectorId == "determinism.fresh-process-canonical");
        Assert.Equal(2, determinism.InvocationCount);
        Assert.True(determinism.FreshProcessPerInvocation);
        Assert.Equal(new[] { "run-1", "run-2" }, determinism.RunIds);
        Assert.True(determinism.CrossCellEquality);
    }

    [Fact]
    public void HostValidation_SupportAndOutcomeSurfacesAreComplete()
    {
        var context = BundleValidator.Validate(Root);
        var vectors = context.Vectors.Vectors.ToDictionary(vector => vector.VectorId, StringComparer.Ordinal);

        Assert.Equal("required", vectors["support.sln"].SupportDisposition);
        Assert.Equal("required", vectors["support.slnx"].SupportDisposition);
        Assert.Equal("required", vectors["support.csproj"].SupportDisposition);
        Assert.Equal("unsupported", vectors["support.slnf"].SupportDisposition);
        Assert.Equal("unsupported", vectors["support.non-csharp-project"].SupportDisposition);
        Assert.Equal("trusted-observed", vectors["support.analyzer"].SupportDisposition);
        Assert.Equal("trusted-observed", vectors["support.generator"].SupportDisposition);
        Assert.Equal("trusted-observed", vectors["support.custom-target"].SupportDisposition);
        Assert.Equal("deferred-owned", vectors["support.multi-targeting"].SupportDisposition);
        Assert.Equal(
            new[] { "compliant", "violation", "skipped" },
            context.Protocol.Taxonomies.AuditOutcome);
        Assert.Equal(
            new[]
            {
                "protected-input-invalidated",
                "protocol-failure",
                "subject-nonconformance",
                "environment-or-infrastructure-incomplete",
                "harness-or-ci-cancelled",
                "harness-or-ci-timed-out",
                "passed"
            },
            context.Protocol.Taxonomies.ValidationPrecedence);
    }

    [Fact]
    public void HostValidation_CrosswalkContainsEveryAuthoritativeIssueAndWorkflowRow()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(FixtureRoot, "requirements-crosswalk.json")));
        var sourceKeys = document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("sourceKey").GetString())
            .ToHashSet(StringComparer.Ordinal);

        for (var index = 1; index <= 12; index++)
        {
            Assert.Contains($"issue-26.acceptance-{index:00}", sourceKeys);
        }
        Assert.Contains("m1-w5.line-177", sourceKeys);
        Assert.Contains("m1-w5.line-179", sourceKeys);
        Assert.Contains("m1-w5.line-181", sourceKeys);
        Assert.Contains("adr-0002.failure-and-cancellation", sourceKeys);
        Assert.Contains("adr-0002.determinism-and-publication", sourceKeys);
        Assert.Contains("adr-0002.input-scope", sourceKeys);
        Assert.Contains("security-boundary.public-ci", sourceKeys);
        Assert.Contains("project-structure.test-harness", sourceKeys);
    }

    [Fact]
    public void HostValidation_StrictJsonAndPublicSafetyFailClosed()
    {
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-hv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var duplicate = Path.Join(temp, "duplicate.json");
            File.WriteAllText(duplicate, "{\"a\":1,\"a\":2}", new UTF8Encoding(false));
            var duplicateFailure = Assert.Throws<ProtocolException>(() =>
            {
                using var _ = CanonicalJson.ReadStrict(duplicate, 1024);
            });
            Assert.Equal("HV109_DUPLICATE_PROPERTY", duplicateFailure.Code);

            var noncanonical = Path.Join(temp, "noncanonical.json");
            File.WriteAllText(noncanonical, "{ \"b\":1,\"a\":2 }\n", new UTF8Encoding(false));
            var canonicalFailure = Assert.Throws<ProtocolException>(() =>
            {
                using var _ = CanonicalJson.ReadStrict(noncanonical, 1024, requireCanonical: true);
            });
            Assert.Equal("HV106_NONCANONICAL_JSON", canonicalFailure.Code);

            var marker = string.Concat("access_token=", "ghp", "-", new string('a', 20));
            var safetyFailure = Assert.Throws<ProtocolException>(() => PublicSafetyScanner.EnsureSafeText(marker));
            Assert.Equal("HV119_PUBLIC_CREDENTIAL_MARKER", safetyFailure.Code);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_RepositoryObserverSeparatesProtectedAndDesignTimeWrites()
    {
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-hv-observer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Join(temp, "Sample.cs"), "class Sample { }\n", new UTF8Encoding(false));
            var before = RepositoryObserver.Capture(temp, ["obj"]);
            File.AppendAllText(Path.Join(temp, "Sample.cs"), "// mutation\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Join(temp, "obj"));
            File.WriteAllText(Path.Join(temp, "obj", "design-time.marker"), "synthetic\n", new UTF8Encoding(false));
            var delta = RepositoryObserver.Compare(before, RepositoryObserver.Capture(temp, ["obj"]));

            Assert.Equal(new[] { "Sample.cs" }, delta.ProtectedChanged);
            Assert.Equal(new[] { "obj/design-time.marker" }, delta.AllowedDesignTimeCreated);
            Assert.True(RepositoryObserver.HasProtectedMutation(delta));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_EvidenceAndReviewValidationRejectUnknownOrMismatchedIdentity()
    {
        var context = BundleValidator.Validate(Root);
        var tempRoot = Path.Join(Root, "TestResults", $"host-validation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var invalidEvidence = Path.Join(tempRoot, "invalid-cell-evidence.json");
            File.WriteAllText(
                invalidEvidence,
                "{\"formatVersion\":\"contractscribe-m1-host-validation-cell-evidence-v1\",\"unknown\":true}\n",
                new UTF8Encoding(false));
            var schemaFailure = Assert.Throws<ProtocolException>(() => SchemaValidation.Validate(
                invalidEvidence,
                Path.Join(Root, "schemas", "validation", "m1-host-validation-cell-evidence-v1.schema.json")));
            Assert.Equal("HV111_SCHEMA_REJECTED", schemaFailure.Code);

            var review = new ReviewRecord(
                "contractscribe-m1-host-validation-review-v1",
                $"review.{new string('0', 64)}",
                context.Lock.BundleId,
                new string('1', 40),
                "independent-relay",
                "00000000-0000-0000-0000-000000000001",
                "00000000-0000-0000-0000-000000000002",
                "accepted",
                [],
                "2026-07-27T00:00:00Z");
            var reviewPath = Path.Join(tempRoot, "review.json");
            CanonicalJson.WriteCanonical(reviewPath, review);
            var reviewFailure = Assert.Throws<ProtocolException>(() =>
                BundleValidator.ValidateReview(Root, reviewPath, context.Lock.BundleId));
            Assert.Equal("HV166_REVIEW_ID_MISMATCH", reviewFailure.Code);

            var acceptedReview = review with { ReviewId = BundleValidator.ComputeReviewId(review) };
            CanonicalJson.WriteCanonical(reviewPath, acceptedReview);
            var commitFailure = Assert.Throws<ProtocolException>(() =>
                BundleValidator.ValidateReview(Root, reviewPath, context.Lock.BundleId));
            Assert.Equal("HV202_REVIEWED_COMMIT_INVALID", commitFailure.Code);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_StaticValidatorsAndPublicSafetyCorpusExecute()
    {
        var context = BundleValidator.Validate(Root);
        foreach (var vector in context.Vectors.Vectors.Where(vector => vector.ExecutorKind == "harness-static"))
        {
            var result = StaticValidatorRegistry.Execute(Root, vector);
            Assert.Equal(vector.ExpectedObservation, result.ObservationCode);
            Assert.Equal(vector.ExpectedEnforcementClass, result.EnforcementClass);
        }

        PublicSafetyScanner.SelfTestMachinePaths();
        PublicSafetyScanner.SelfTestCredentialMarkers();
        var bearer = Assert.Throws<ProtocolException>(() =>
            PublicSafetyScanner.EnsureSafeText(
                string.Concat("Authorization:", " Bearer synthetic-value")));
        Assert.Equal("HV119_PUBLIC_CREDENTIAL_MARKER", bearer.Code);
        var unsupportedClaim = Assert.Throws<ProtocolException>(() =>
            PublicSafetyScanner.EnsureNoUnsupportedClaims(
                "The harness guarantees network isolation for this run."));
        Assert.Equal("HV199_PUBLIC_UNSUPPORTED_CLAIM", unsupportedClaim.Code);
        foreach (var claim in new[]
        {
            "Network isolation is enforced.",
            "The execution ran in an egress sandbox.",
            "Repository-controlled MSBuild cannot access secrets.",
            "ContractScribe guarantees network isolation.",
            "The host enforces credential isolation.",
            "This validation is fully offline and sandboxed.",
            "The host blocks all outbound connections.",
            "Validation prevents outbound access.",
            "Outbound network traffic is blocked."
        })
        {
            Assert.Equal(
                "HV199_PUBLIC_UNSUPPORTED_CLAIM",
                Assert.Throws<ProtocolException>(() =>
                    PublicSafetyScanner.EnsureNoUnsupportedClaims(claim)).Code);
        }
        PublicSafetyScanner.EnsureNoUnsupportedClaims(
            "This protocol does not claim network isolation and is not an egress sandbox.");
        PublicSafetyScanner.EnsureNoUnsupportedClaims(
            "This protocol does not block outbound access.");
    }

    [Fact]
    public void HostValidation_ProductionCommandContractRejectsPrefixesAndLiteralArguments()
    {
        var subjectSchema = Path.Join(
            Root,
            "schemas",
            "validation",
            "m1-host-validation-subject-v1.schema.json");
        var template = JsonNode.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "execution-subject.template.json")))!.AsObject();
        template["cells"]![0]!["argumentPrefix"] = new JsonArray("adaptable-command");
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-subject-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temp, template.ToJsonString(), new UTF8Encoding(false));
            Assert.Equal(
                "HV111_SCHEMA_REJECTED",
                Assert.Throws<ProtocolException>(() =>
                    SchemaValidation.Validate(temp, subjectSchema)).Code);
        }
        finally
        {
            File.Delete(temp);
        }

        var context = BundleValidator.Validate(Root);
        var protocolPath = "tests/fixtures/m1-host-validation/v1/protocol.json";
        var protocolIdentity = new ArtifactIdentity(
            protocolPath,
            CanonicalJson.Sha256File(Path.Join(
                Root,
                protocolPath.Replace('/', Path.DirectorySeparatorChar))));
        var cell = new ExecutionCell(
            new CellMaterialization(
                "ubuntu-x64",
                "1",
                "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
                "synthetic",
                "linux-x64",
                "X64",
                "10.0.102",
                "10.0.0",
                "18.0.0",
                [protocolIdentity, protocolIdentity]),
            "dotnet-dll",
            protocolPath,
            [],
            []);
        var fixture = new FixtureRealization(
            "failure.runtime-load-before-entry",
            "external-process",
            "tests/fixtures",
            new string('1', 64),
            true,
            null,
            "dotnet",
            ["repository:m1-host-validation/v1/protocol.json", "Release"],
            null,
            [protocolIdentity],
            [],
            "bounded-polling",
            null,
            null,
            []);
        Assert.Equal(
            "HV206_EXECUTOR_ARRANGEMENT_MISMATCH",
            Assert.Throws<ProtocolException>(() =>
                CellExecutor.ValidateExecutorArrangement(
                    Root,
                    Path.Join(Root, "tests", "fixtures"),
                    cell,
                    fixture,
                    allowMaterializationDrift: false)).Code);
    }

    [Fact]
    public void HostValidation_AuditResultSemanticClosureRejectsEmptyAndContradictoryResults()
    {
        var validPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "golden",
            "required-present.canonical.json");
        using (var valid = CanonicalJson.ReadStrict(validPath, 2 * 1024 * 1024))
        {
            AuditResultSemanticValidator.Validate(Root, valid.RootElement);
        }

        var emptyPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "empty-results.json");
        using (var empty = CanonicalJson.ReadStrict(emptyPath, 2 * 1024 * 1024))
        {
            Assert.Equal(
                "HV230_AUDIT_RESULT_SEMANTICS",
                Assert.Throws<ProtocolException>(() =>
                    AuditResultSemanticValidator.Validate(Root, empty.RootElement)).Code);
        }

        var contradiction = JsonNode.Parse(File.ReadAllText(validPath))!.AsObject();
        contradiction["results"]![0]!["auditOutcome"] = "audit.outcome.violation";
        using var contradictoryDocument = JsonDocument.Parse(contradiction.ToJsonString());
        Assert.Equal(
            "HV230_AUDIT_RESULT_SEMANTICS",
            Assert.Throws<ProtocolException>(() =>
                AuditResultSemanticValidator.Validate(
                    Root,
                    contradictoryDocument.RootElement)).Code);
    }

    [Fact]
    public void HostValidation_AuditResultSemanticClosureEnforcesTaxonomyRegistryConstraints()
    {
        var componentPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "classification-not-applicable.json");
        using (var component = CanonicalJson.ReadStrict(componentPath, 2 * 1024 * 1024))
        {
            AuditResultSemanticValidator.Validate(Root, component.RootElement);
        }

        AssertInvalidTaxonomyMutation(componentPath, root =>
        {
            var classification = root["results"]![0]!["classification"]!.AsObject();
            classification["supportStatus"] = "support.supported";
            classification.Remove("skipReason");
        });
        AssertInvalidTaxonomyMutation(componentPath, root =>
            root["results"]![0]!["classification"]!["origin"] = "origin.source");
        AssertInvalidTaxonomyMutation(componentPath, root =>
            root["results"]![0]!["classification"]!["parentSymbolRef"]!["documentationCommentId"]
                = "M:AuditFixtures.NotApplicableWidget.Run");

        var unknownPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "classification-skipped.json");
        AssertInvalidTaxonomyMutation(unknownPath, root =>
        {
            var classification = root["results"]![0]!["classification"]!.AsObject();
            classification["supportStatus"] = "support.supported";
            classification.Remove("skipReason");
        });
    }

    [Fact]
    public void HostValidation_HandledFailuresRequireOneExactProtectedRegistryRow()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, evidence) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var run = CreateMatchedRun(
            context,
            subject,
            "failure.invalid-input",
            "run-1",
            101,
            '2');

        ValidateWithReplacement(context, subject, evidence, run);
        foreach (var replacement in new[]
        {
            run.Subject! with { FailureCode = "host.unknown-code" },
            run.Subject! with { FailureStage = "environment" },
            run.Subject! with { ExecutionOutcome = "audit-error" },
            run.Subject! with { TerminalState = "cancelled" }
        })
        {
            var exception = Assert.Throws<ProtocolException>(() =>
                ValidateWithReplacement(
                    context,
                    subject,
                    evidence,
                    run with { Subject = replacement }));
            Assert.Equal("HV157_FAILURE_REGISTRY_BINDING", exception.Code);
        }
    }

    [Fact]
    public void HostValidation_ControlEvidenceRequiresExactGateActionAndPostGateSample()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, evidence) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var vector = context.Vectors.Vectors.Single(candidate =>
            candidate.VectorId == "toolchain.process-topology");
        var fixture = subject.Cells[0].Fixtures.Single(candidate =>
            candidate.VectorId == vector.VectorId) with
        {
            CapabilityAvailable = true,
            BlockedReasonCode = null,
            ProcessIdentityRegistry = []
        };
        var baseline = CreateMatchedRun(
            context,
            subject,
            vector.VectorId,
            "run-1",
            701,
            '1') with
        {
            Process = new ProcessObservation(
                0,
                "started",
                "normal",
                false,
                true,
                true,
                "process-observation",
                "observe",
                true)
        };
        foreach (var process in new[]
        {
            baseline.Process with { ObservedGateName = "before-commit" },
            baseline.Process with { ObservedControlAction = "cancel" },
            baseline.Process with { PostGateSampleObserved = false }
        })
        {
            var derived = RunSemantics.Derive(
                context,
                vector,
                baseline with { Process = process },
                fixture,
                subject.SourceConfiguration);
            Assert.Contains("HV224_CONTROL_GATE_INCOMPLETE", derived.DiagnosticCodes);
        }
    }

    [Fact]
    public void HostValidation_ProtectedProcessIdentityDistinguishesToolchainWorkerAndRestore()
    {
        var entryPoint = Path.Join(
            Root,
            "tests",
            "ContractScribe.HostValidation",
            "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release",
            "net10.0",
            "ContractScribe.HostValidation.dll");
        Assert.True(File.Exists(entryPoint));
        var fingerprint = ProcessTreeObserver.ComputeIdentityFingerprint(
            "dotnet",
            entryPoint,
            ["build"]);
        Assert.Equal(
            "contractscribe-worker",
            ProcessTreeObserver.ClassifyIdentity(
                "dotnet",
                entryPoint,
                ["build"],
                [new ProcessIdentityRule(fingerprint)]));
        Assert.Equal(
            "contractscribe-worker",
            ProcessTreeObserver.ClassifyIdentity(
                "dotnet",
                entryPoint,
                ["restore"],
                [new ProcessIdentityRule(fingerprint)]));
        var restoreFingerprint = ProcessTreeObserver.ComputeIdentityFingerprint(
            "dotnet",
            entryPoint,
            ["restore"]);
        Assert.Equal(
            "restore-or-runtime-download",
            ProcessTreeObserver.ClassifyIdentity(
                "dotnet",
                entryPoint,
                ["restore"],
                [new ProcessIdentityRule(restoreFingerprint)]));

        var toolchainRoot = Path.Join(Path.GetTempPath(), $"contractscribe-toolchain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolchainRoot);
        var toolchainEntryPoint = Path.Join(toolchainRoot, "MSBuild.dll");
        try
        {
            File.Copy(entryPoint, toolchainEntryPoint);
            var toolchainFingerprint = ProcessTreeObserver.ComputeIdentityFingerprint(
                "dotnet",
                toolchainEntryPoint,
                ["build"]);
            Assert.Equal(
                "toolchain-owned",
                ProcessTreeObserver.ClassifyIdentity(
                    "dotnet",
                    toolchainEntryPoint,
                    ["build"],
                    [new ProcessIdentityRule(toolchainFingerprint)]));
        }
        finally
        {
            Directory.Delete(toolchainRoot, recursive: true);
        }
        Assert.Equal(
            "contractscribe-worker",
            ProcessTreeObserver.ClassifyIdentity("dotnet", entryPoint, [], []));
        Assert.Equal(
            "unknown-descendant",
            ProcessTreeObserver.ClassifyIdentity("msbuild", entryPoint, [], []));

        var context = BundleValidator.Validate(Root);
        var (subject, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var vector = context.Vectors.Vectors.Single(candidate =>
            candidate.VectorId == "toolchain.no-automatic-restore");
        var fixture = subject.Cells[0].Fixtures.Single(candidate =>
            candidate.VectorId == vector.VectorId) with
        {
            CapabilityAvailable = true,
            BlockedReasonCode = null,
            ProcessIdentityRegistry = []
        };
        var baseline = CreateMatchedRun(
            context,
            subject,
            vector.VectorId,
            "run-1",
            702,
            '2') with
        {
            Process = new ProcessObservation(
                0,
                "started",
                "normal",
                false,
                true,
                true,
                "process-observation",
                "observe",
                true)
        };
        var permitted = baseline with
        {
            ObservedProcesses =
            [
                baseline.ObservedProcesses[0],
                new ObservedProcess(703, 702, "toolchain-owned", "msbuild")
            ]
        };
        Assert.Equal(
            vector.ExpectedObservation,
            RunSemantics.Derive(
                context,
                vector,
                permitted,
                fixture,
                subject.SourceConfiguration).Observation);
        var restore = permitted with
        {
            ObservedProcesses =
            [
                baseline.ObservedProcesses[0],
                new ObservedProcess(
                    704,
                    702,
                    "restore-or-runtime-download",
                    "dotnet")
            ]
        };
        Assert.Equal(
            "toolchain.restore-or-runtime-download-marker-observed",
            RunSemantics.Derive(
                context,
                vector,
                restore,
                fixture,
                subject.SourceConfiguration).Observation);
    }

    [Fact]
    public void HostValidation_SupersededAttemptsMustShareHostAndWorkflowLineage()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var current = subject.ValidationAttempt with
        {
            WorkflowRunId = "2",
            RunAttempt = 1
        };
        var candidate = subject.ValidationAttempt with
        {
            WorkflowRunId = "1",
            RunAttempt = 1
        };
        foreach (var mismatched in new[]
        {
            candidate with { HostRevision = new string('2', 40) },
            candidate with { Workflow = "tests/other-workflow.yml" },
            candidate with { WorkflowRevision = new string('2', 64) }
        })
        {
            Assert.Equal(
                "HV221_SUPERSEDES_INVALID",
                Assert.Throws<ProtocolException>(() =>
                    EvidenceValidator.ValidateSupersededAttemptIdentity(
                        Root,
                        subject.SourceConfiguration,
                        current,
                        mismatched)).Code);
        }
    }

    [Fact]
    public void HostValidation_CommitBoundEnumerationDetectsMaterializedDeletion()
    {
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-git-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(temp, "src"));
        try
        {
            File.WriteAllText(Path.Join(temp, "ContractScribe.slnx"), "<Solution />\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Join(temp, "src", "A.cs"), "class A {}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Join(temp, "src", "B.cs"), "class B {}\n", new UTF8Encoding(false));
            RunGit(temp, "init");
            RunGit(temp, "config", "user.email", "host-validation@example.invalid");
            RunGit(temp, "config", "user.name", "Host Validation");
            RunGit(temp, "add", "src");
            RunGit(temp, "commit", "-m", "fixture");
            var revision = RunGit(temp, "rev-parse", "HEAD").Trim();
            File.Delete(Path.Join(temp, "src", "B.cs"));

            Assert.Equal(
                ["src/A.cs", "src/B.cs"],
                BundleValidator.ExpandCommitBoundPaths(temp, revision, ["src"]));
            Assert.Equal(
                ["src/A.cs"],
                BundleValidator.ExpandProtectedInputPaths(temp, ["src"]));
        }
        finally
        {
            NormalizeFileAttributes(temp);
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_EvidenceMutationCorpusIsClosedAndExecutable()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(FixtureRoot, "evidence-mutation-corpus.json")));
        Assert.Equal(
            "contractscribe-m1-host-validation-evidence-mutation-corpus-v1",
            document.RootElement.GetProperty("formatVersion").GetString());
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(25, cases.Length);
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("caseId").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("seed").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("target").GetString()));
            var operation = item.GetProperty("operation").GetString()
                ?? throw new InvalidOperationException("Mutation operation is required.");
            var expected = item.GetProperty("expectedCode").GetString();
            var failure = Assert.Throws<ProtocolException>(() => ExecuteMutationCase(operation));
            Assert.Equal(expected, failure.Code);
        });
    }

    [Fact]
    public void HostValidation_OutputGuardRejectsProtectedAndInputCollisions()
    {
        var context = BundleValidator.Validate(Root);
        var subject = Path.Join(Root, "TestResults", "m1-host-validation", "subject.json");
        var collision = Assert.Throws<ProtocolException>(() =>
            OutputPathGuard.Validate(context, [subject], subject));
        Assert.Equal("HV194_OUTPUT_PATH_COLLISION", collision.Code);

        var protectedOutput = Path.Join(Root, "src", "ContractScribe.Core", "forbidden.json");
        var protectedFailure = Assert.Throws<ProtocolException>(() =>
            OutputPathGuard.Validate(context, [], protectedOutput));
        Assert.Equal("HV204_OUTPUT_PROTECTED", protectedFailure.Code);
    }

    [Fact]
    public void HostValidation_ExternalExecutorCommandsMatchFrozenExecutableAndArgumentSequence()
    {
        var protocolPath = "tests/fixtures/m1-host-validation/v1/protocol.json";
        var protocolIdentity = new ArtifactIdentity(
            protocolPath,
            CanonicalJson.Sha256File(Path.Join(Root, protocolPath.Replace('/', Path.DirectorySeparatorChar))));
        var cell = new ExecutionCell(
            new CellMaterialization(
                "ubuntu-x64",
                "1",
                "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
                "synthetic",
                "linux-x64",
                "X64",
                "10.0.102",
                "10.0.0",
                "18.0.0",
                [protocolIdentity, protocolIdentity]),
            "dotnet-dll",
            protocolPath,
            [],
            []);
        var helperPath =
            "tests/fixtures/.contractscribe-validation/process.runtime-load-invalid/invalid-entrypoint.dll";
        var helperIdentity = new ArtifactIdentity(helperPath, new string('1', 64));
        var arrangementIdentity = new ArtifactIdentity(
            "tests/fixtures/.contractscribe-validation/process.runtime-load-invalid/arrangement.json",
            new string('2', 64));
        var fixture = new FixtureRealization(
            "failure.runtime-load-before-entry",
            "external-process",
            "tests/fixtures",
            new string('1', 64),
            true,
            null,
            "dotnet",
            [
                "repository:.contractscribe-validation/process.runtime-load-invalid/invalid-entrypoint.dll",
                "{request}",
                "{response}"
            ],
            null,
            [helperIdentity, arrangementIdentity],
            [],
            "bounded-polling",
            null,
            null);

        CellExecutor.ValidateExecutorArrangement(
            Root,
            Path.Join(Root, "tests", "fixtures"),
            cell,
            fixture,
            allowMaterializationDrift: true);

        foreach (var mutation in new[]
        {
            fixture with
            {
                Executable = "repository:m1-host-validation/v1/protocol.json",
                ExecutableSha256 = new string('2', 64)
            },
            fixture with { Arguments = [.. fixture.Arguments, "{control}"] },
            fixture with { Arguments = fixture.Arguments.Skip(1).ToArray() },
            fixture with
            {
                Arguments =
                [
                    fixture.Arguments[0],
                    fixture.Arguments[2],
                    fixture.Arguments[1]
                ]
            },
            fixture with { ArrangementInputs = [arrangementIdentity, helperIdentity] },
            fixture with { ArrangementInputs = [helperIdentity] }
        })
        {
            Assert.Equal(
                "HV206_EXECUTOR_ARRANGEMENT_MISMATCH",
                Assert.Throws<ProtocolException>(() =>
                    CellExecutor.ValidateExecutorArrangement(
                        Root,
                        Path.Join(Root, "tests", "fixtures"),
                        cell,
                        mutation,
                        allowMaterializationDrift: true)).Code);
        }
    }

    [Fact]
    public void HostValidation_PreEntryFailuresDoNotRequireANonexistentManagedPid()
    {
        var context = BundleValidator.Validate(Root);
        var sha = new string('1', 64);
        var artifact = new ArtifactIdentity("src/ContractScribe.Core/ContractScribe.Core.csproj", sha);
        var failureRegistry = new ArtifactIdentity(
            "tests/fixtures/m1-host-validation/v1/self-test-host-failure-registry.json",
            sha);
        var source = new SubjectSourceConfiguration(
            $"source.{sha}",
            new string('1', 40),
            ["src"],
            [artifact],
            failureRegistry,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact);
        var cases = new[]
        {
            ("failure.launch-before-entry", "launch-failure"),
            ("failure.runtime-load-before-entry", "runtime-load-failure"),
            ("failure.permission-before-entry", "permission-failure")
        };
        foreach (var (vectorId, processStart) in cases)
        {
            var vector = context.Vectors.Vectors.Single(candidate => candidate.VectorId == vectorId);
            Assert.False(vector.FreshProcessPerInvocation);
            var fixture = new FixtureRealization(
                vectorId,
                vector.ExecutorKind,
                "tests/fixtures",
                sha,
                true,
                null,
                vectorId == "failure.launch-before-entry" ? "missing-executable" : "dotnet",
                [],
                null,
                [artifact],
                [],
                "bounded-polling",
                null,
                null);
            var run = new RunEvidence(
                vectorId,
                "run-1",
                "matched",
                vector.ExpectedObservation,
                vector.ExpectedObservation,
                vector.ExpectedEnforcementClass,
                vector.ExpectedEnforcementClass,
                null,
                new ProcessObservation(null, processStart, "not-started", false, true, true),
                null,
                null,
                new RepositoryDelta([], [], [], [], [], [], [], [], []),
                [],
                []);
            var derived = RunSemantics.Derive(context, vector, run, fixture, source);
            Assert.Equal("matched", derived.Verdict);
            Assert.Equal(vector.ExpectedObservation, derived.Observation);
        }
    }

    [Fact]
    public void HostValidation_OutputGuardRejectsExternalSymlinkIntoRepository()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-output-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var alias = Path.Join(temp, "repository-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, Path.Join(Root, "src"));
            var output = Path.Join(alias, "ContractScribe.Core", "forbidden.json");
            Assert.Equal(
                "HV205_OUTPUT_LINK_ALIAS",
                Assert.Throws<ProtocolException>(() =>
                    OutputPathGuard.Validate(context, [], output)).Code);
        }
        catch (Exception exception) when (
            OperatingSystem.IsWindows()
            && exception is UnauthorizedAccessException or IOException)
        {
            return;
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_CellSemanticMutationCorpusRejectsFalsePasses()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, evidence) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        EvidenceValidator.ValidateCellSemantics(context, subject, evidence);

        var missing = evidence with { Runs = evidence.Runs.Skip(1).ToArray() };
        Assert.Equal(
            "HV154_EVIDENCE_EXECUTION_SET",
            Assert.Throws<ProtocolException>(() =>
                EvidenceValidator.ValidateCellSemantics(context, subject, missing)).Code);

        var firstBlocked = evidence.Runs.First(run => run.Verdict == "vector-environment-blocked");
        var falseMatch = evidence with
        {
            Runs = evidence.Runs.Select(run => run == firstBlocked
                ? run with { Verdict = "matched" }
                : run).ToArray()
        };
        Assert.Equal(
            "HV156_FALSE_MATCH",
            Assert.Throws<ProtocolException>(() =>
                EvidenceValidator.ValidateCellSemantics(context, subject, falseMatch)).Code);

        var falseOutcome = evidence with { Outcome = "passed" };
        Assert.Equal(
            "HV213_FALSE_CELL_OUTCOME",
            Assert.Throws<ProtocolException>(() =>
                EvidenceValidator.ValidateCellSemantics(context, subject, falseOutcome)).Code);
    }

    [Fact]
    public void HostValidation_ExecutableSelfTestPassesWithoutRawDiagnostics()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var harness = Path.Join(
            Root,
            "tests",
            "ContractScribe.HostValidation",
            "bin",
            configuration,
            "net10.0",
            "ContractScribe.HostValidation.dll");
        Assert.True(File.Exists(harness), $"Host-validation harness was not built at {harness}.");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add("self-test");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(Root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the host-validation harness.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Host-validation self-test timed out.");

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("HV000_SELF_TEST", stdout.Trim());
        Assert.Equal(string.Empty, stderr);
    }

    private static void ExecuteMutationCase(string operation)
    {
        var context = BundleValidator.Validate(Root);
        var (subject, ubuntu) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var (_, windows) = CreateSyntheticIncompleteCell(context, "windows-x64");
        var ordinary = CreateMatchedRun(context, subject, "failure.invalid-input", "run-1", 101, '2');
        var firstBlocked = ubuntu.Runs.First(run => run.Verdict == "vector-environment-blocked");

        switch (operation)
        {
            case "replace-subject-vector-id":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with { Subject = ordinary.Subject! with { VectorId = "failure.load-failure" } });
                return;
            case "replace-subject-run-id":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with { Subject = ordinary.Subject! with { RunId = "run-2" } });
                return;
            case "replace-subject-process-start":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with { Subject = ordinary.Subject! with { ProcessStart = "launch-failure" } });
                return;
            case "replace-subject-process-termination":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with { Subject = ordinary.Subject! with { ProcessTermination = "crash" } });
                return;
            case "replace-subject-result-digest":
                {
                    var resultRun = CreateMatchedRun(
                        context,
                        subject,
                        "determinism.fresh-process-canonical",
                        "run-1",
                        102,
                        '3');
                    var different = resultRun.ObservedCanonicalResult! with { Sha256 = new string('4', 64) };
                    ValidateWithReplacement(
                        context,
                        subject,
                        ubuntu,
                        resultRun with { Subject = resultRun.Subject! with { CanonicalResult = different } });
                    return;
                }
            case "replace-failure-registry-digest":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with
                    {
                        Subject = ordinary.Subject! with { FailureRegistryIdentity = new string('9', 64) }
                    });
                return;
            case "replace-blocked-verdict-with-matched":
                EvidenceValidator.ValidateCellSemantics(
                    context,
                    subject,
                    ubuntu with
                    {
                        Runs = ubuntu.Runs.Select(run => run == firstBlocked
                            ? run with { Verdict = "matched" }
                            : run).ToArray()
                    });
                return;
            case "replace-cell-outcome-with-passed":
                EvidenceValidator.ValidateCellSemantics(context, subject, ubuntu with { Outcome = "passed" });
                return;
            case "remove-required-run":
                EvidenceValidator.ValidateCellSemantics(
                    context,
                    subject,
                    ubuntu with { Runs = ubuntu.Runs.Skip(1).ToArray() });
                return;
            case "duplicate-required-run":
                EvidenceValidator.ValidateCellSemantics(
                    context,
                    subject,
                    ubuntu with { Runs = ubuntu.Runs.Append(ubuntu.Runs[0]).ToArray() });
                return;
            case "replace-run-vector-with-unknown":
                EvidenceValidator.ValidateCellSemantics(
                    context,
                    subject,
                    ubuntu with
                    {
                        Runs = ubuntu.Runs.Select((run, index) =>
                            index == 0 ? run with { VectorId = "unknown.vector" } : run).ToArray()
                    });
                return;
            case "replace-second-result-digest":
                {
                    var run1 = CreateMatchedRun(
                        context,
                        subject,
                        "determinism.fresh-process-canonical",
                        "run-1",
                        110,
                        '5');
                    var run2 = CreateMatchedRun(
                        context,
                        subject,
                        "determinism.fresh-process-canonical",
                        "run-2",
                        111,
                        '6');
                    EvidenceValidator.ValidateCellSemantics(
                        context,
                        EnableFixture(subject, ubuntu.Cell.CellId, run1.VectorId),
                        ReplaceRuns(ubuntu, run1, run2));
                    return;
                }
            case "reuse-subject-process-id":
                {
                    var run1 = CreateMatchedRun(
                        context,
                        subject,
                        "determinism.fresh-process-canonical",
                        "run-1",
                        120,
                        '7');
                    var run2 = CreateMatchedRun(
                        context,
                        subject,
                        "determinism.fresh-process-canonical",
                        "run-2",
                        120,
                        '7');
                    EvidenceValidator.ValidateCellSemantics(
                        context,
                        EnableFixture(subject, ubuntu.Cell.CellId, run1.VectorId),
                        ReplaceRuns(ubuntu, run1, run2));
                    return;
                }
            case "add-protected-write":
                ValidateWithReplacement(
                    context,
                    subject,
                    ubuntu,
                    ordinary with
                    {
                        RepositoryDelta = ordinary.RepositoryDelta with
                        {
                            ProtectedChanged = ["src/ContractScribe.Core/ContractScribe.Core.csproj"]
                        }
                    });
                return;
            case "replace-cell-evidence-digest":
            case "replace-aggregate-outcome-with-passed":
                {
                    var expectedCells = new[]
                    {
                        new CellAggregate("ubuntu-x64", new string('a', 64), ubuntu.Outcome),
                        new CellAggregate("windows-x64", new string('b', 64), windows.Outcome)
                    };
                    var aggregate = CreateSyntheticAggregate(ubuntu, expectedCells);
                    if (operation == "replace-cell-evidence-digest")
                    {
                        aggregate = aggregate with
                        {
                            Cells =
                            [
                                expectedCells[0] with { EvidenceSha256 = new string('c', 64) },
                                expectedCells[1]
                            ]
                        };
                    }
                    else
                    {
                        aggregate = aggregate with { Outcome = "passed" };
                    }
                    EvidenceValidator.ValidateAggregateDerivation(
                        aggregate,
                        ubuntu,
                        expectedCells,
                        [],
                        "environment-or-infrastructure-incomplete");
                    return;
                }
            case "replace-second-cell-attempt":
                EvidenceValidator.ValidateAggregateCellSemantics(
                    context,
                    [
                        ubuntu,
                        windows with
                        {
                            ValidationAttempt = windows.ValidationAttempt with { RunAttempt = 2 }
                        }
                    ]);
                return;
            case "replace-second-cell-result-digest":
                {
                    var ubuntuDeterminism = ReplaceRuns(
                        ubuntu,
                        CreateMatchedRun(context, subject, "determinism.fresh-process-canonical", "run-1", 130, 'd'),
                        CreateMatchedRun(context, subject, "determinism.fresh-process-canonical", "run-2", 131, 'd'));
                    var windowsDeterminism = ReplaceRuns(
                        windows,
                        CreateMatchedRun(context, subject, "determinism.fresh-process-canonical", "run-1", 140, 'e'),
                        CreateMatchedRun(context, subject, "determinism.fresh-process-canonical", "run-2", 141, 'e'));
                    EvidenceValidator.ValidateAggregateCellSemantics(
                        context,
                        [ubuntuDeterminism, windowsDeterminism]);
                    return;
                }
            case "replace-review-id":
            case "replace-classification":
                {
                    var sha = new string('1', 64);
                    var review = new ReviewRecord(
                        "contractscribe-m1-host-validation-review-v1",
                        $"review.{sha}",
                        context.Lock.BundleId,
                        new string('1', 40),
                        "independent-relay",
                        "11111111-1111-1111-1111-111111111111",
                        "22222222-2222-2222-2222-222222222222",
                        "accepted",
                        [],
                        "2026-01-01T00:00:00Z");
                    var incomplete = new IncompleteEvidence(
                        "contractscribe-m1-host-validation-incomplete-evidence-v1",
                        context.Lock.BundleId,
                        review.ReviewId,
                        subject.SourceConfiguration.SourceConfigurationId,
                        subject.ValidationAttempt,
                        "ubuntu-x64",
                        "protocol-failure",
                        ["HV999_INTERNAL_ERROR"],
                        true);
                    incomplete = operation == "replace-review-id"
                        ? incomplete with { ReviewId = $"review.{new string('2', 64)}" }
                        : incomplete with { Classification = "protected-input-invalidated" };
                    EvidenceValidator.ValidateIncompleteSemantics(context, review, subject, incomplete);
                    return;
                }
            case "insert-windows-rooted-path":
                PublicSafetyScanner.EnsureSafeText(string.Concat("C:", @"\agent\_work\result.json"));
                return;
            case "insert-unix-rooted-path":
                PublicSafetyScanner.EnsureSafeText(string.Concat("/", "srv/build/result.json"));
                return;
            case "insert-bearer-credential":
                PublicSafetyScanner.EnsureSafeText(
                    string.Concat("Authorization:", " Bearer synthetic-value"));
                return;
            case "reuse-input-as-output":
                {
                    var path = Path.Join(Root, "TestResults", "m1-host-validation", "subject.json");
                    OutputPathGuard.Validate(context, [path], path);
                    return;
                }
            case "select-protected-source-output":
                OutputPathGuard.Validate(
                    context,
                    [],
                    Path.Join(Root, "src", "ContractScribe.Core", "forbidden.json"));
                return;
            default:
                throw new InvalidOperationException($"Unknown mutation operation: {operation}");
        }
    }

    private static void ValidateWithReplacement(
        BundleContext context,
        ExecutionSubjectManifest subject,
        CellEvidence evidence,
        RunEvidence replacement) =>
        EvidenceValidator.ValidateCellSemantics(
            context,
            EnableFixture(subject, evidence.Cell.CellId, replacement.VectorId),
            ReplaceRuns(evidence, replacement));

    private static void AssertInvalidTaxonomyMutation(
        string path,
        Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(root);
        using var document = JsonDocument.Parse(root.ToJsonString());
        Assert.Equal(
            "HV230_AUDIT_RESULT_SEMANTICS",
            Assert.Throws<ProtocolException>(() =>
                AuditResultSemanticValidator.Validate(Root, document.RootElement)).Code);
    }

    private static ExecutionSubjectManifest EnableFixture(
        ExecutionSubjectManifest subject,
        string cellId,
        string vectorId) =>
        subject with
        {
            Cells = subject.Cells.Select(cell => cell.Materialization.CellId != cellId
                ? cell
                : cell with
                {
                    Fixtures = cell.Fixtures.Select(fixture => fixture.VectorId != vectorId
                        ? fixture
                        : fixture with
                        {
                            CapabilityAvailable = true,
                            BlockedReasonCode = null
                        }).ToArray()
                }).ToArray()
        };

    private static CellEvidence ReplaceRuns(CellEvidence evidence, params RunEvidence[] replacements)
    {
        var byKey = replacements.ToDictionary(
            run => $"{run.VectorId}\0{run.RunId}",
            StringComparer.Ordinal);
        return evidence with
        {
            Runs = evidence.Runs.Select(run =>
                byKey.TryGetValue($"{run.VectorId}\0{run.RunId}", out var replacement)
                    ? replacement
                    : run).ToArray()
        };
    }

    private static RunEvidence CreateMatchedRun(
        BundleContext context,
        ExecutionSubjectManifest subjectManifest,
        string vectorId,
        string runId,
        int processId,
        char digestCharacter)
    {
        var vector = context.Vectors.Vectors.Single(candidate => candidate.VectorId == vectorId);
        var fixture = subjectManifest.Cells[0].Fixtures.Single(candidate => candidate.VectorId == vectorId) with
        {
            CapabilityAvailable = true,
            BlockedReasonCode = null
        };
        var hasResult = vectorId == "determinism.fresh-process-canonical";
        var commitment = hasResult
            ? new CanonicalResultCommitment(
                new string(digestCharacter, 64),
                100,
                "canonical-json-utf8-no-bom-single-lf",
                true)
            : null;
        var facts = hasResult
            ? new ObservedAuditResultFacts(
                1,
                1,
                1,
                "profile.external-api",
                ["audit.outcome.compliant"])
            : null;
        var isFailure = vectorId == "failure.invalid-input";
        var subject = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vectorId,
            runId,
            "started",
            "normal",
            isFailure ? null : "compliant",
            isFailure ? "invalid-input" : "succeeded",
            isFailure ? subjectManifest.SourceConfiguration.FailureRegistry.Sha256 : null,
            isFailure ? "host.invalid-input" : null,
            isFailure ? "input" : null,
            "committed",
            isFailure ? "absent" : "published",
            vector.ExpectedEnforcementClass,
            vector.ExpectedObservation,
            commitment);
        return new RunEvidence(
            vectorId,
            runId,
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            subject,
            new ProcessObservation(0, "started", "normal", false, true, true),
            commitment,
            facts,
            new RepositoryDelta([], [], [], [], [], [], [], [], []),
            [new ObservedProcess(processId, 1, "subject-runtime", "dotnet")],
            []);
    }

    private static AggregateEvidence CreateSyntheticAggregate(
        CellEvidence baseline,
        IReadOnlyList<CellAggregate> cells) =>
        new(
            "contractscribe-m1-host-validation-aggregate-evidence-v1",
            baseline.BundleId,
            baseline.ReviewId,
            baseline.SourceConfigurationId,
            baseline.ValidationAttempt,
            new AggregateFinalizationIdentity(
                "incomplete",
                baseline.ValidationAttempt.ValidationExecutionSha),
            cells,
            "environment-or-infrastructure-incomplete",
            []);

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
        throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private static void NormalizeFileAttributes(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static (ExecutionSubjectManifest Subject, CellEvidence Evidence) CreateSyntheticIncompleteCell(
        BundleContext context,
        string cellId)
    {
        var sha = new string('1', 64);
        var commit = new string('1', 40);
        var artifact = new ArtifactIdentity("src/ContractScribe.Core/ContractScribe.Core.csproj", sha);
        var failureRegistry = new ArtifactIdentity(
            "tests/fixtures/m1-host-validation/v1/self-test-host-failure-registry.json",
            sha);
        var source = new SubjectSourceConfiguration(
            $"source.{sha}",
            commit,
            ["src/ContractScribe.Core"],
            [artifact],
            failureRegistry,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact);
        var attempt = new ValidationAttemptIdentity(
            ".github/workflows/ci.yml",
            sha,
            "1",
            1,
            commit,
            commit);
        var protocolCell = context.Protocol.RequiredCells.Single(cell => cell.CellId == cellId);
        var materialization = new CellMaterialization(
            cellId,
            "synthetic-job",
            "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
            "synthetic-image",
            protocolCell.Rid,
            protocolCell.Architecture,
            "10.0.102",
            "10.0.0",
            "18.0.0",
            [artifact, new ArtifactIdentity("src/ContractScribe.Cli/ContractScribe.Cli.csproj", sha)]);
        var vectors = context.Vectors.Vectors
            .Where(vector => vector.Cells.Contains(cellId, StringComparer.Ordinal))
            .ToArray();
        var fixtures = vectors.Where(vector => vector.ExecutorKind != "harness-static")
            .Select(vector => new FixtureRealization(
                vector.VectorId,
                vector.ExecutorKind,
                "tests/fixtures",
                sha,
                false,
                "HV900_SYNTHETIC_CAPABILITY",
                null,
                [],
                null,
                [],
                [],
                "bounded-polling",
                null,
                null))
            .ToArray();
        var executionCell = new ExecutionCell(
            materialization,
            "dotnet-dll",
            "src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll",
            [],
            fixtures);
        var subject = new ExecutionSubjectManifest(
            "contractscribe-m1-host-validation-execution-subject-v1",
            context.Lock.BundleId,
            "production-host",
            "issue-24",
            "prebuilt-in-process-test-entrypoint",
            source,
            attempt,
            [executionCell]);
        var fixturesByVector = fixtures.ToDictionary(fixture => fixture.VectorId, StringComparer.Ordinal);
        var runs = new List<RunEvidence>();
        foreach (var vector in vectors)
        {
            foreach (var runId in vector.RunIds)
            {
                if (vector.ExecutorKind == "harness-static")
                {
                    var result = StaticValidatorRegistry.Execute(Root, vector);
                    runs.Add(new RunEvidence(
                        vector.VectorId,
                        runId,
                        "matched",
                        vector.ExpectedObservation,
                        result.ObservationCode,
                        vector.ExpectedEnforcementClass,
                        result.EnforcementClass,
                        null,
                        new ProcessObservation(null, "not-started", "not-started", false, true, true),
                        null,
                        null,
                        new RepositoryDelta([], [], [], [], [], [], [], [], []),
                        [],
                        []));
                    continue;
                }
                var fixture = fixturesByVector[vector.VectorId];
                var provisional = new RunEvidence(
                    vector.VectorId,
                    runId,
                    "vector-environment-blocked",
                    vector.ExpectedObservation,
                    "vector.capability-unavailable",
                    vector.ExpectedEnforcementClass,
                    vector.ExpectedEnforcementClass,
                    null,
                    new ProcessObservation(null, "not-started", "not-started", false, true, true),
                    null,
                    null,
                    new RepositoryDelta([], [], [], [], [], [], [], [], []),
                    [],
                    ["HV900_SYNTHETIC_CAPABILITY"]);
                var derived = RunSemantics.Derive(context, vector, provisional, fixture, source);
                runs.Add(provisional with
                {
                    Verdict = derived.Verdict,
                    ObservedObservation = derived.Observation,
                    ObservedEnforcementClass = derived.EnforcementClass,
                    DiagnosticCodes = derived.DiagnosticCodes
                });
            }
        }
        var evidence = new CellEvidence(
            "contractscribe-m1-host-validation-cell-evidence-v1",
            context.Lock.BundleId,
            $"review.{sha}",
            source.SourceConfigurationId,
            sha,
            attempt,
            materialization,
            runs.OrderBy(run => run.VectorId, StringComparer.Ordinal)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .ToArray(),
            "environment-or-infrastructure-incomplete");
        return (subject, evidence);
    }
}
