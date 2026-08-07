using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

[CollectionDefinition("M1 host validation environment", DisableParallelization = true)]
public sealed class M1HostValidationEnvironmentCollection;

[Collection("M1 host validation environment")]
public sealed class M1HostValidationProtocolTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string FixtureRoot = Path.Join(Root, "tests", "fixtures", "m1-host-validation", "v1");
    private sealed record SyntheticSubject(
        CommonSourceManifest Common,
        IReadOnlyList<ExecutionCell> Cells)
    {
        public SubjectSourceConfiguration SourceConfiguration => Common.SourceConfiguration;

        public ValidationAttemptIdentity ValidationAttempt => Common.ValidationAttempt;

        public CellSubjectManifest CellManifest(string cellId) =>
            new(
                "contractscribe-m1-host-validation-cell-subject-v1",
                CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(Common)),
                cellId,
                Cells.Single(cell => cell.Materialization.CellId == cellId));
    }

    [Fact]
    public void HostValidation_BundleIsClosedAndCurrent()
    {
        var context = BundleValidator.Validate(Root);

        Assert.Equal("m1-host-validation-v1", context.Protocol.ProtocolId);
        Assert.Equal(
            "https://github.com/SolusQuest/contract-scribe/issues/75",
            context.Protocol.Baseline.CoordinatingIssue);
        Assert.Equal(
            "m1-host-validation-content-bound-execution-v1",
            context.Protocol.Baseline.ContractRevision);
        Assert.Equal(
            new PredecessorBaselineIdentity(
                "https://github.com/SolusQuest/contract-scribe/issues/70",
                "issue-70-host-validation-baseline-lineage-v1",
                "67c149fbc105d2ccae94becd6b2158b68027cbfd",
                "tests/fixtures/m1-contract-baseline/v1/manifest.json",
                "4ca9d7d7ba60650a1a3838486fc80f6d44e22cfbf451f07c47e4aa4796d5c7b2"),
            context.Protocol.Baseline.Predecessor);
        Assert.Matches("^m1hvp1\\.[0-9a-f]{64}$", context.Lock.BundleId);
        Assert.Equal(
            context.Protocol.ArtifactInventory.Order(StringComparer.Ordinal),
            context.Lock.Entries.Select(entry => entry.Path));
        Assert.DoesNotContain(
            context.Lock.Entries,
            entry => File.ReadAllText(Path.Join(Root, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
                .Contains(context.Lock.BundleId, StringComparison.Ordinal));

        var protectedInputs = CanonicalJson.DeserializeStrict<ProtectedInputManifest>(
            Path.Join(FixtureRoot, "protected-inputs.json"),
            4 * 1024 * 1024,
            requireCanonical: true);
        foreach (var protectedTestPath in new[]
                 {
                     "tests/ContractScribe.Tests/M1ContractBaselineHostConsumerTests.cs",
                     "tests/ContractScribe.Tests/M1HostValidationProtocolTests.cs"
                 })
        {
            Assert.Contains(protectedTestPath, protectedInputs.Roots);
            Assert.Contains(
                protectedInputs.Entries,
                entry => entry.Path == protectedTestPath);
        }
    }

    [Fact]
    public void HostValidation_CheckedInReviewHasOneCanonicalLifecycleShape()
    {
        var context = BundleValidator.Validate(Root);
        var review = BundleValidator.ValidateReviewStructure(
            Root,
            BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);

        Assert.Equal(context.Lock.BundleId, review.BundleId);
        Assert.Equal(BundleValidator.ComputeReviewId(review), review.ReviewId);
        switch (review.Verdict)
        {
            case "pending":
                AssertCanonicalPendingReview(review);
                AssertPendingReview(() =>
                    BundleValidator.Validate(Root, requireReview: true));
                break;
            case "accepted":
                AssertCanonicalAcceptedReview(review);
                Assert.Equal(
                    context.Lock.BundleId,
                    BundleValidator.Validate(Root, requireReview: true).Lock.BundleId);
                break;
            default:
                Assert.Fail($"Unexpected checked-in review verdict: {review.Verdict}");
                break;
        }

        var obsolete = CreatePendingReviewRecord(context.Lock.BundleId) with
        {
            BlockingFindingIds = ["baseline.main-reconciliation-pending"],
            ReviewId = string.Empty
        };
        obsolete = obsolete with { ReviewId = BundleValidator.ComputeReviewId(obsolete) };
        var temp = Path.Join(
            Root,
            "TestResults",
            $"contractscribe-pending-review-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            CanonicalJson.WriteCanonical(temp, obsolete);
            Assert.Equal(
                "HV111_SCHEMA_REJECTED",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.ValidateReviewStructure(
                        Root,
                        temp,
                        context.Lock.BundleId)).Code);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void HostValidation_PendingAndAcceptedReviewsShareOneProtectedBundle()
    {
        var context = BundleValidator.Validate(Root);
        var protectedInputsPath = RepositoryPaths.ResolveConfined(
            Root,
            BundleValidator.ProtectedInputsRelativePath);
        var protectedInputsIdentity = CanonicalJson.Sha256File(protectedInputsPath);
        var temp = Path.Join(
            Root,
            "TestResults",
            $"contractscribe-review-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var pendingPath = Path.Join(temp, "pending.json");
        var acceptedPath = Path.Join(temp, "accepted.json");
        try
        {
            CanonicalJson.WriteCanonical(
                pendingPath,
                CreatePendingReviewRecord(context.Lock.BundleId));
            CanonicalJson.WriteCanonical(
                acceptedPath,
                CreateAcceptedReview(context.Lock.BundleId, new string('f', 40)));

            var pending = BundleValidator.ValidateReviewStructure(
                Root,
                pendingPath,
                context.Lock.BundleId);
            var accepted = BundleValidator.ValidateReview(
                Root,
                acceptedPath,
                context.Lock.BundleId);

            AssertCanonicalPendingReview(pending);
            AssertCanonicalAcceptedReview(accepted);
            Assert.Equal(pending.BundleId, accepted.BundleId);
            AssertPendingReview(() =>
                BundleValidator.Validate(
                    Root,
                    requireReview: true,
                    reviewPath: pendingPath));
            Assert.Equal(
                context.Lock.BundleId,
                BundleValidator.Validate(
                    Root,
                    requireReview: true,
                    reviewPath: acceptedPath).Lock.BundleId);
            Assert.Equal(
                protectedInputsIdentity,
                CanonicalJson.Sha256File(protectedInputsPath));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_AcceptedReviewAuthorizesBundleWithoutGitObjectLookup()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            $"contractscribe-content-review-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            var absentOrUnrelatedSourceRevision = new string('f', 40);
            CanonicalJson.WriteCanonical(
                temp,
                CreateAcceptedReview(context.Lock.BundleId, absentOrUnrelatedSourceRevision));

            var accepted = BundleValidator.ValidateReview(
                Root,
                temp,
                context.Lock.BundleId);

            Assert.Equal("accepted", accepted.Verdict);
            Assert.Equal(absentOrUnrelatedSourceRevision, accepted.ReviewedSourceRevision);
            Assert.Equal(context.Lock.BundleId, accepted.BundleId);

            CanonicalJson.WriteCanonical(
                temp,
                CreateAcceptedReview(
                    $"m1hvp1.{new string('0', 64)}",
                    absentOrUnrelatedSourceRevision));
            Assert.Equal(
                "HV121_REVIEW_NOT_ACCEPTED",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.ValidateReview(Root, temp, context.Lock.BundleId)).Code);

            CanonicalJson.WriteCanonical(
                temp,
                CreateAcceptedReview(context.Lock.BundleId, new string('G', 40)));
            Assert.Equal(
                "HV111_SCHEMA_REJECTED",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.ValidateReview(Root, temp, context.Lock.BundleId)).Code);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void HostValidation_MaterializerUsesExecutionCheckoutNotReviewRevision()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            "m1-host-validation",
            $"materializer-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var reviewPath = Path.Join(temp, "review.json");
        var outputPath = Path.Join(temp, SubjectManifestMaterializer.CommonFileName);
        var executionRevision = RunGit(Root, "rev-parse", "HEAD").Trim();
        try
        {
            CanonicalJson.WriteCanonical(
                reviewPath,
                CreateAcceptedReview(context.Lock.BundleId, new string('f', 40)));
            using var environment = GitHubEnvironment(executionRevision);

            var manifest = SubjectManifestMaterializer.MaterializeCommon(
                Root,
                reviewPath,
                outputPath);

            Assert.Equal(executionRevision, manifest.SourceConfiguration.HostRevision);
            Assert.Equal(executionRevision, manifest.ValidationAttempt.HostRevision);
            Assert.Equal(executionRevision, manifest.ValidationAttempt.ValidationExecutionSha);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_MaterializerRejectsMissingOrDriftedExecutionCommit()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            "m1-host-validation",
            $"materializer-execution-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var reviewPath = Path.Join(temp, "review.json");
        try
        {
            CanonicalJson.WriteCanonical(
                reviewPath,
                CreateAcceptedReview(
                    context.Lock.BundleId,
                    RunGit(Root, "rev-parse", "HEAD").Trim()));
            foreach (var executionRevision in new[]
            {
                new string('f', 40),
                RunGit(Root, "rev-parse", "HEAD~1").Trim()
            })
            {
                var output = Path.Join(temp, $"{executionRevision}.json");
                using var environment = GitHubEnvironment(executionRevision);
                _ = Assert.Throws<ProtocolException>(() =>
                    SubjectManifestMaterializer.MaterializeCommon(
                        Root,
                        reviewPath,
                        output));
                Assert.False(File.Exists(output));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_CellMaterializerClosesAttemptBeforeAnyMutation()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            "m1-host-validation",
            $"materializer-pretrust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var reviewPath = Path.Join(temp, "review.json");
        var commonPath = Path.Join(temp, SubjectManifestMaterializer.CommonFileName);
        var executionRevision = RunGit(Root, "rev-parse", "HEAD").Trim();
        var cellId = OperatingSystem.IsWindows() ? "windows-x64" : "ubuntu-x64";
        var vector = context.Vectors.Vectors.First(candidate =>
            candidate.ExecutorKind == "production-host"
            && candidate.Cells.Contains(cellId, StringComparer.Ordinal));
        var fixtureRoot = RepositoryPaths.ResolveConfined(
            Root,
            $"tests/fixtures/m1-host-validation/runtime/{cellId}/{vector.VectorId}",
            mustExist: false);
        try
        {
            CanonicalJson.WriteCanonical(
                reviewPath,
                CreateAcceptedReview(context.Lock.BundleId, new string('f', 40)));
            using var environment = GitHubEnvironment(executionRevision);
            _ = SubjectManifestMaterializer.MaterializeCommon(Root, reviewPath, commonPath);
            Assert.False(Directory.Exists(fixtureRoot));

            var mutations = new (string Name, string? Value, string RequestedCell)[]
            {
                ("GITHUB_RUN_ID", "9002", cellId),
                ("GITHUB_RUN_ATTEMPT", "2", cellId),
                ("GITHUB_SHA", new string('e', 40), cellId),
                ("GITHUB_WORKFLOW_REF", "SolusQuest/contract-scribe/.github/workflows/other.yml@refs/heads/test", cellId),
                ("GITHUB_JOB", "other-job", cellId),
                ("RUNNER_OS", OperatingSystem.IsWindows() ? "Linux" : "Windows", cellId),
                ("RUNNER_ARCH", "ARM64", cellId),
                ("GITHUB_JOB", "host-validation-cell", cellId == "windows-x64" ? "ubuntu-x64" : "windows-x64")
            };
            foreach (var mutation in mutations)
            {
                var original = Environment.GetEnvironmentVariable(mutation.Name);
                var output = Path.Join(temp, $"cell-{Guid.NewGuid():N}.json");
                File.WriteAllText(output, "unchanged", new UTF8Encoding(false));
                try
                {
                    Environment.SetEnvironmentVariable(mutation.Name, mutation.Value);
                    Assert.Equal(
                        "HV211_EXECUTION_ENVIRONMENT_UNBOUND",
                        Assert.Throws<ProtocolException>(() =>
                            SubjectManifestMaterializer.MaterializeCell(
                                Root,
                                reviewPath,
                                commonPath,
                                mutation.RequestedCell,
                                output)).Code);
                    Assert.Equal("unchanged", File.ReadAllText(output));
                    Assert.False(Directory.Exists(fixtureRoot));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(mutation.Name, original);
                }
            }
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                FixtureRecipeRegistry.RemoveProvisionedReparsePoints(fixtureRoot);
                Directory.Delete(fixtureRoot, recursive: true);
            }
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_LockedByteDriftFailsBeforeReviewAuthorization()
    {
        var context = BundleValidator.Validate(Root);
        var first = context.Lock.Entries[0];
        var driftedEntries = context.Lock.Entries
            .Select(entry => entry == first
                ? entry with { Sha256 = new string('0', 64) }
                : entry)
            .ToArray();
        var drifted = context.Lock with
        {
            BundleId = BundleValidator.ComputeBundleId(driftedEntries),
            Entries = driftedEntries
        };

        Assert.Equal(
            "HV134_ARTIFACT_HASH_MISMATCH",
            Assert.Throws<ProtocolException>(() =>
                BundleValidator.ValidateLock(Root, context.Protocol, drifted)).Code);
    }

    [Fact]
    public void HostValidation_PendingReviewBlocksEvidenceBeforeInputReadsOrOutputCreation()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            $"host-validation-pending-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var reviewPath = Path.Join(temp, "pending-review.json");
        var missing = Path.Join(temp, "missing.json");
        var output = Path.Join(temp, "aggregate-evidence.json");
        try
        {
            CanonicalJson.WriteCanonical(
                reviewPath,
                CreatePendingReviewRecord(context.Lock.BundleId));
            AssertPendingReview(() =>
                SubjectManifestMaterializer.MaterializeCommon(
                    Root,
                    reviewPath,
                    output));
            Assert.False(File.Exists(output));

            AssertPendingReview(() =>
                CellExecutor.RunAsync(
                    Root,
                    missing,
                    missing,
                    reviewPath,
                    output).GetAwaiter().GetResult());
            Assert.False(File.Exists(output));

            AssertPendingReview(() =>
                EvidenceValidator.Aggregate(
                    Root,
                    Path.Join(temp, "missing-artifact-root"),
                    output,
                    reviewPath,
                    new string('1', 40)));
            Assert.False(File.Exists(output));

            AssertPendingReview(() =>
                EvidenceValidator.PreparePublicArtifact(
                    Root,
                    "aggregate",
                    missing,
                    output,
                    reviewPath,
                    Path.Join(temp, "missing-artifact-root")));
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
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

        var materializedCells = context.Protocol.RequiredCells.Select(required =>
            new ExecutionCell(
                new CellMaterialization(
                    required.CellId,
                    "synthetic-job",
                    "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
                    "synthetic-image",
                    required.Rid,
                    required.Architecture,
                    "10.0.102",
                    "10.0.0",
                    "18.0.0",
                    [],
                    [],
                    []),
                "dotnet-dll",
                "src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll",
                [],
                context.Vectors.Vectors
                    .Where(vector => vector.ExecutorKind != "harness-static"
                        && vector.Cells.Contains(required.CellId, StringComparer.Ordinal))
                    .Select(vector => FrozenFixtureRegistry.Materialize(Root, required.CellId, vector))
                    .ToArray())).ToArray();
        foreach (var cell in materializedCells)
        {
            foreach (var fixture in cell.Fixtures)
            {
                FrozenFixtureRegistry.Validate(
                    cell.Materialization.CellId,
                    context.Vectors.Vectors.Single(vector =>
                        vector.VectorId == fixture.VectorId),
                    fixture);
            }
        }
        var workingFixture = materializedCells[0].Fixtures.Single(fixture =>
            fixture.VectorId == "path.working-directory-independent");
        Assert.Equal(
            ["repository-root", "system-temp"],
            workingFixture.RunWorkingDirectories.Select(item => item.Mode));
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
            "support.multi-targeting-rejected-no-partial-result",
            vectors["support.multi-targeting"].ExpectedObservation);
        Assert.DoesNotContain(
            "canonical-bytes",
            vectors["support.multi-targeting"].ObserverRequirements);
        Assert.Equal(
            ("forced-termination", "external-kill"),
            RunSemantics.ExpectedControl("bounds.forced-termination"));
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
    public void HostValidation_AuditResultObservationUsesTheNormativeCanonicalizer()
    {
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-hv-audit-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var golden = Path.Join(
                Root,
                "tests",
                "fixtures",
                "audit-result",
                "v1",
                "golden",
                "required-present.canonical.json");
            var resultPath = Path.Join(temp, "result.json");
            File.Copy(golden, resultPath);

            var observed = CellExecutor.ObserveCanonicalResult(Root, temp, "result.json");
            Assert.NotNull(observed.Commitment);
            Assert.True(observed.Commitment.Canonical);
            using (var goldenDocument = JsonDocument.Parse(File.ReadAllBytes(golden)))
            {
                Assert.Equal(
                    ContractScribe.ContractBaselineProbe.AuditResultCanonicalizer.Canonicalize(
                        goldenDocument.RootElement),
                    AuditResultV1Canonicalizer.Canonicalize(goldenDocument.RootElement));
            }

            var source = JsonNode.Parse(File.ReadAllText(golden))!.AsObject();
            var alphabetic = new JsonObject
            {
                ["auditResultVersion"] = source["auditResultVersion"]!.DeepClone(),
                ["policyConfigurationVersion"] = source["policyConfigurationVersion"]!.DeepClone(),
                ["results"] = source["results"]!.DeepClone(),
                ["targetProfile"] = source["targetProfile"]!.DeepClone(),
                ["taxonomyRegistryVersion"] = source["taxonomyRegistryVersion"]!.DeepClone()
            };
            File.WriteAllText(
                resultPath,
                alphabetic.ToJsonString() + "\n",
                new UTF8Encoding(false));
            Assert.Equal(
                "HV106_NONCANONICAL_JSON",
                Assert.Throws<ProtocolException>(() =>
                    CellExecutor.ObserveCanonicalResult(Root, temp, "result.json")).Code);
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
            Assert.Equal(
                Encoding.UTF8.GetByteCount("synthetic\n"),
                delta.AllowedDesignTimeCreatedOrChangedBytes);
            Assert.True(RepositoryObserver.HasProtectedMutation(delta));

            Directory.CreateDirectory(Path.Join(temp, "src"));
            File.WriteAllText(
                Path.Join(temp, "src", "Hidden.cs"),
                "class Hidden { }\n",
                new UTF8Encoding(false));
            var broadAllowlist = RepositoryObserver.Capture(temp, ["src"]);
            Assert.Contains("src/Hidden.cs", broadAllowlist.ProtectedFiles.Keys);
            Assert.DoesNotContain("src/Hidden.cs", broadAllowlist.AllowedDesignTimeFiles.Keys);
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
            var validatedReview = BundleValidator.ValidateReview(
                Root,
                reviewPath,
                context.Lock.BundleId);
            Assert.Equal(acceptedReview.ReviewId, validatedReview.ReviewId);
            Assert.Equal(acceptedReview.BundleId, validatedReview.BundleId);
            Assert.Equal(
                acceptedReview.BlockingFindingIds,
                validatedReview.BlockingFindingIds);
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
        var exactClaimBlock = NetworkClaimSetRegistry.RenderProtocolBlock();
        PublicSafetyScanner.EnsureNoUnsupportedClaims(exactClaimBlock);
        var unsupportedClaim = Assert.Throws<ProtocolException>(() =>
            PublicSafetyScanner.EnsureNoUnsupportedClaims(
                $"{exactClaimBlock}\nThe harness guarantees network isolation for this run."));
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
            "Outbound network traffic is blocked.",
            "ContractScribe cannot reach the Internet.",
            "The validator has no external connectivity.",
            "The runtime cannot connect to the Internet.",
            "The audit cannot access the network.",
            "Outbound connections are impossible.",
            "Internet access is disabled.",
            "The runtime is air-gapped.",
            "The runtime is disconnected from the Internet."
        })
        {
            Assert.Equal(
                "HV199_PUBLIC_UNSUPPORTED_CLAIM",
                Assert.Throws<ProtocolException>(() =>
                    PublicSafetyScanner.EnsureNoUnsupportedClaims(
                        $"{exactClaimBlock}\n{claim}")).Code);
        }
        foreach (var mutation in new[]
                 {
                     context.Protocol.PublicSafety with
                     {
                         NetworkClaimSetMembers =
                             context.Protocol.PublicSafety.NetworkClaimSetMembers.Skip(1).ToArray()
                     },
                     context.Protocol.PublicSafety with
                     {
                         NetworkClaimSetMembers =
                             context.Protocol.PublicSafety.NetworkClaimSetMembers
                                 .Append(context.Protocol.PublicSafety.NetworkClaimSetMembers[0])
                                 .ToArray()
                     },
                     context.Protocol.PublicSafety with
                     {
                         NetworkClaimSetMembers =
                             context.Protocol.PublicSafety.NetworkClaimSetMembers.Reverse().ToArray()
                     },
                     context.Protocol.PublicSafety with
                     {
                         NetworkClaimSetMembers =
                         [
                             context.Protocol.PublicSafety.NetworkClaimSetMembers[0] with
                             {
                                 Text = "The runtime is disconnected from the Internet."
                             },
                             .. context.Protocol.PublicSafety.NetworkClaimSetMembers.Skip(1)
                         ]
                     }
                 })
        {
            Assert.Equal(
                "HV131_PUBLIC_SAFETY_POLICY",
                Assert.Throws<ProtocolException>(() =>
                    NetworkClaimSetRegistry.Validate(mutation)).Code);
        }
        var cleanNetworkMethods = context.NetworkEvidenceProfile.Methods
            .Select(method => new NetworkEvidenceMethodResult(
                method.MethodId,
                method.MethodVersion,
                "input.0000000000000000000000000000000000000000000000000000000000000000",
                method.CoverageLimitationId,
                "complete",
                "network.method-clean",
                null))
            .ToArray();
        var cleanNetwork = new NetworkEvidenceObservation(
            context.NetworkEvidenceProfile.ProfileId,
            NetworkClaimSetRegistry.ClaimSetId,
            cleanNetworkMethods);
        Assert.Equal(
            "matched",
            NetworkEvidenceEvaluator.Classify(
                context.NetworkEvidenceProfile,
                cleanNetwork).Verdict);
        foreach (var (status, cause, expectedVerdict) in new[]
                 {
                     ("finding", "subject-nonconformance", "subject-nonconformance"),
                     ("incomplete", "subject-nonconformance", "subject-nonconformance"),
                     ("incomplete", "protocol-failure", "protocol-invalid-observation"),
                     ("incomplete", "environment-or-infrastructure-incomplete", "vector-infrastructure-incomplete")
                 })
        {
            var methods = cleanNetworkMethods.ToArray();
            methods[0] = methods[0] with
            {
                Status = status,
                ObservationCode = "network.synthetic-nonclean",
                CauseClass = cause
            };
            Assert.Equal(
                expectedVerdict,
                NetworkEvidenceEvaluator.Classify(
                    context.NetworkEvidenceProfile,
                    cleanNetwork with { Methods = methods }).Verdict);
        }
        var protectedMethods = cleanNetworkMethods.ToArray();
        protectedMethods[1] = protectedMethods[1] with
        {
            Status = "incomplete",
            ObservationCode = "network.synthetic-protected-input-drift",
            CauseClass = "protected-input-invalidated"
        };
        Assert.Equal(
            "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED",
            Assert.Throws<ProtocolException>(() =>
                NetworkEvidenceEvaluator.Classify(
                    context.NetworkEvidenceProfile,
                    cleanNetwork with { Methods = protectedMethods })).Code);
        var reorderedMethods = cleanNetworkMethods.Reverse().ToArray();
        Assert.Equal(
            "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED",
            Assert.Throws<ProtocolException>(() =>
                NetworkEvidenceEvaluator.Classify(
                    context.NetworkEvidenceProfile,
                    cleanNetwork with { Methods = reorderedMethods })).Code);
        var wrongVersionMethods = cleanNetworkMethods.ToArray();
        wrongVersionMethods[0] = wrongVersionMethods[0] with { MethodVersion = 2 };
        Assert.Equal(
            "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED",
            Assert.Throws<ProtocolException>(() =>
                NetworkEvidenceEvaluator.Classify(
                    context.NetworkEvidenceProfile,
                    cleanNetwork with { Methods = wrongVersionMethods })).Code);
        var wrongCauseMethods = cleanNetworkMethods.ToArray();
        wrongCauseMethods[0] = wrongCauseMethods[0] with
        {
            Status = "finding",
            ObservationCode = "network.synthetic-invalid-cause",
            CauseClass = "protocol-failure"
        };
        Assert.Equal(
            "protocol-invalid-observation",
            NetworkEvidenceEvaluator.Classify(
                context.NetworkEvidenceProfile,
                cleanNetwork with { Methods = wrongCauseMethods }).Verdict);
        var wrongInputMethods = cleanNetworkMethods.ToArray();
        wrongInputMethods[1] = wrongInputMethods[1] with
        {
            InputIdentity =
                "closure.1111111111111111111111111111111111111111111111111111111111111111"
        };
        Assert.Equal(
            "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED",
            Assert.Throws<ProtocolException>(() =>
                NetworkEvidenceEvaluator.Classify(
                    context.NetworkEvidenceProfile,
                    cleanNetwork with { Methods = wrongInputMethods },
                    cleanNetworkMethods.Select(method => method.InputIdentity).ToArray())).Code);
        Assert.Equal(
            "protected-input-invalidated",
            NetworkEvidenceEvaluator.ClassifyBoundedScanFailure(
                new ProtocolException("HV244_PRODUCTION_DEPENDENCY_CLOSURE"),
                "invalidated").CauseClass);
        Assert.Equal(
            "environment-or-infrastructure-incomplete",
            NetworkEvidenceEvaluator.ClassifyBoundedScanFailure(
                new IOException("synthetic observer failure"),
                "current").CauseClass);
        var runtimeManifestFailure =
            NetworkEvidenceEvaluator.ClassifyBoundedScanFailure(
                new ProtocolException("HV249_SELECTED_RUNTIME_MANIFEST"),
                "current");
        Assert.Equal(
            "environment-or-infrastructure-incomplete",
            runtimeManifestFailure.CauseClass);
        Assert.Equal(
            "network.selected-runtime-manifest-incomplete",
            runtimeManifestFailure.ObservationCode);
        Assert.Equal(
            "subject-nonconformance",
            NetworkEvidenceEvaluator.ClassifyBoundedScanFailure(
                new BadImageFormatException("synthetic subject artifact"),
                "current").CauseClass);
        Assert.Equal(
            "protocol-failure",
            NetworkEvidenceEvaluator.ClassifyBoundedScanFailure(
                new ProtocolException("HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"),
                "current").CauseClass);
        foreach (var source in new[]
                 {
                     "using System.Net.Http;",
                     "var client = new HttpClient();",
                     "await Dns.GetHostEntryAsync(\"example.invalid\");",
                     "var socket = new System.Net.Sockets.Socket(default, default, default);",
                     "var type = Type.GetType(\"System.Net.Http.HttpClient, System.Net.Http\");",
                     "var client = Activator.CreateInstance(type!);",
                      "client!.GetType().GetMethod(\"GetAsync\")!.Invoke(client, null);",
                      "var type = typeof(object).Assembly.GetType(name);",
                      "var methods = type!.GetMethods();",
                      "var constructors = type!.GetConstructors();",
                      "var callback = methods[0].CreateDelegate(delegateType);",
                      "var compiled = expression.Compile();",
                     "var assembly = Assembly.LoadFrom(path);",
                     "var library = NativeLibrary.Load(path);",
                     "[DllImport(\"native\")] static extern void Send();",
                     "dynamic client = CreateClient();"
                 })
        {
            Assert.Equal(
                "HV232_NETWORK_OPERATION_SOURCE",
                Assert.Throws<ProtocolException>(() =>
                    NetworkOperationSourceScanner.ValidateSyntheticSource(source)).Code);
        }
        NetworkOperationSourceScanner.ValidateSyntheticSource(
            "// HttpClient in a comment is not an operation.\nvar text = \"System.Net.Http\";");
    }

    [Fact]
    public void HostValidation_NativeInteropAllowlistAcceptsOnlyExactProtectedBoundary()
    {
        foreach (var relative in new[]
        {
            "src/ContractScribe.Roslyn/AtomicResultPublisher.cs",
            "src/ContractScribe.Roslyn/ToolchainProcessMeter.cs",
            "src/ContractScribe.Roslyn/DotnetSdkResolver.cs"
        })
        {
            var source = RepositoryPaths.ResolveConfined(Root, relative);
            Assert.False(NetworkOperationSourceScanner.HasForbiddenSourceOperation(
                relative,
                source));
            var mutated = Path.Join(
                Root,
                "TestResults",
                "m1-host-validation",
                $"native-source-{Guid.NewGuid():N}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(mutated)!);
            try
            {
                File.WriteAllText(
                    mutated,
                    File.ReadAllText(source) + "\n",
                    new UTF8Encoding(false));
                Assert.True(NetworkOperationSourceScanner.HasForbiddenSourceOperation(
                    relative,
                    mutated));
            }
            finally
            {
                File.Delete(mutated);
            }
        }

        var assemblyPath = Path.Join(
            Root,
            "src",
            "ContractScribe.Cli",
            "bin",
            "Release",
            "net10.0",
            "ContractScribe.Roslyn.dll");
        Assert.False(NetworkOperationSourceScanner.HasForbiddenMetadataOperation(assemblyPath));
        Assert.True(NativeInteropAllowlist.IsAllowedMetadataInterop(
            "ContractScribe.Roslyn",
            "ContractScribe.Roslyn.StablePublicationDirectory",
            "CreateFileW",
            "kernel32.dll",
            "CreateFileW",
            (System.Reflection.MethodImportAttributes)324,
            "00071280950e090918090918"));
        foreach (var mutation in new[]
        {
            (Type: "ContractScribe.Roslyn.OtherType", Library: "kernel32.dll", Entry: "CreateFileW", Signature: "00071280950e090918090918"),
            (Type: "ContractScribe.Roslyn.StablePublicationDirectory", Library: "ws2_32.dll", Entry: "CreateFileW", Signature: "00071280950e090918090918"),
            (Type: "ContractScribe.Roslyn.StablePublicationDirectory", Library: "kernel32.dll", Entry: "connect", Signature: "00071280950e090918090918"),
            (Type: "ContractScribe.Roslyn.StablePublicationDirectory", Library: "kernel32.dll", Entry: "CreateFileW", Signature: "00010808")
        })
        {
            Assert.False(NativeInteropAllowlist.IsAllowedMetadataInterop(
                "ContractScribe.Roslyn",
                mutation.Type,
                "CreateFileW",
                mutation.Library,
                mutation.Entry,
                (System.Reflection.MethodImportAttributes)324,
                mutation.Signature));
        }
    }

    [Fact]
    public void HostValidation_DeclaredNetworkInventoryIsDerivedFromExactInputs()
    {
        foreach (var (inputClass, text) in new[]
                 {
                     (
                         "project-package",
                         "<Project><ItemGroup><PackageReference Include=\"Octokit\" /></ItemGroup></Project>"),
                     (
                         "configuration-json",
                         "{\"networkOperations\":[\"provider\"]}"),
                     (
                         "configuration-xml",
                         "<configuration><telemetryEnabled>true</telemetryEnabled></configuration>"),
                     (
                         "configuration-text",
                         "runtimeDownloadEnabled: true"),
                     (
                         "command-contract",
                         "{\"arguments\":[\"--provider=openai\"]}"),
                     (
                         "environment-policy",
                         "ContractScribe initiates provider requests over the network."),
                     (
                         "workflow",
                         "steps:\n  - run: dotnet ContractScribe.dll audit --provider=openai")
                 })
        {
            Assert.True(
                DeclaredNetworkOperationInventoryEvaluator.HasSyntheticDeclaration(
                    inputClass,
                    text));
        }
        Assert.False(
            DeclaredNetworkOperationInventoryEvaluator.HasSyntheticDeclaration(
                "environment-policy",
                "ContractScribe does not call the model provider or GitHub API."));
        Assert.False(
            DeclaredNetworkOperationInventoryEvaluator.HasSyntheticDeclaration(
                "workflow",
                "steps:\n  - run: dotnet restore ContractScribe.slnx\n  - uses: actions/download-artifact@v4"));

        var temp = Path.Join(
            Root,
            "TestResults",
            $"host-validation-declared-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var projectPath = Path.Join(temp, "Host.csproj");
            var configurationPath = Path.Join(temp, "host.json");
            var commandPath = Path.Join(temp, "command.json");
            var policyPath = Path.Join(temp, "policy.md");
            var workflowPath = Path.Join(temp, "workflow.yml");
            File.WriteAllText(
                projectPath,
                "<Project><ItemGroup><PackageReference Include=\"JsonSchema.Net\" /></ItemGroup></Project>",
                new UTF8Encoding(false));
            File.WriteAllText(configurationPath, "{}", new UTF8Encoding(false));
            File.WriteAllText(commandPath, "{\"arguments\":[]}", new UTF8Encoding(false));
            File.WriteAllText(
                policyPath,
                "ContractScribe does not call the model provider or GitHub API.",
                new UTF8Encoding(false));
            File.WriteAllText(
                workflowPath,
                "steps:\n  - run: dotnet restore ContractScribe.slnx\n",
                new UTF8Encoding(false));
            ArtifactIdentity Identity(string path) => new(
                RepositoryPaths.ToRepositoryRelative(Root, path),
                CanonicalJson.Sha256File(path));
            var project = Identity(projectPath);
            var configuration = Identity(configurationPath);
            var command = Identity(commandPath);
            var policy = Identity(policyPath);
            var workflow = Identity(workflowPath);
            var source = new SubjectSourceConfiguration(
                $"source.{new string('1', 64)}",
                new string('2', 40),
                $"operations.{new string('3', 64)}",
                [],
                [project, configuration],
                configuration,
                configuration,
                project,
                command,
                configuration,
                policy,
                workflow);
            var cleanInventoryId =
                BundleValidator.ComputeDeclaredOperationInventoryId(source);
            Assert.False(
                DeclaredNetworkOperationInventoryEvaluator.HasDeclaredNetworkOperation(
                    Root,
                    source));

            File.WriteAllText(
                configurationPath,
                "{\"networkOperations\":[\"provider\"]}",
                new UTF8Encoding(false));
            var changedConfiguration = Identity(configurationPath);
            var changed = source with
            {
                SourceAndBuildInputs = [project, changedConfiguration],
                FailureRegistry = changedConfiguration,
                CalibratedBounds = changedConfiguration,
                ContractBaseline = changedConfiguration
            };
            Assert.NotEqual(
                cleanInventoryId,
                BundleValidator.ComputeDeclaredOperationInventoryId(changed));
            Assert.True(
                DeclaredNetworkOperationInventoryEvaluator.HasDeclaredNetworkOperation(
                    Root,
                    changed));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_FixtureRecipesFreezeMaterializedBytes()
    {
        var context = BundleValidator.Validate(Root);
        var representatives = new[]
        {
            "support.sln",
            "support.multi-targeting",
            "failure.runtime-load-before-entry",
            "failure.permission-before-entry",
            "path.symlink-escape",
            "path.junction-reparse-escape",
            "publication.same-directory-atomic",
            "publication.cross-volume-rejected"
        };
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-hv-fixture-recipes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var cellId in context.Protocol.RequiredCells.Select(cell => cell.CellId))
            {
                foreach (var vectorId in representatives)
                {
                    var vector = context.Vectors.Vectors.Single(item => item.VectorId == vectorId);
                    var fixtureRoot = Path.Join(temp, cellId, vectorId);
                    FixtureRecipeRegistry.Provision(fixtureRoot, cellId, vector);
                    Assert.Equal(
                        FixtureRecipeRegistry.ExpectedRepositoryIdentity(cellId, vector),
                        CellExecutor.ComputeRepositoryIdentity(
                            RepositoryObserver.Capture(fixtureRoot, ["obj"])));
                }
            }
        }
        finally
        {
            FixtureRecipeRegistry.RemoveProvisionedReparsePoints(temp);
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_FixturePreparationProvidesOnlyRequiredRestoreAssetsAndResetsRuns()
    {
        var context = BundleValidator.Validate(Root);
        var cellId = OperatingSystem.IsWindows() ? "windows-x64" : "ubuntu-x64";
        var vectorIds = new[]
        {
            "contracts.policy-conformance",
            "toolchain.missing-assets",
            "toolchain.no-automatic-restore",
            "determinism.fresh-process-canonical"
        };
        try
        {
            foreach (var vectorId in vectorIds)
            {
                var vector = context.Vectors.Vectors.Single(candidate =>
                    candidate.VectorId == vectorId);
                var repositoryRoot = RepositoryPaths.ResolveConfined(
                    Root,
                    $"tests/fixtures/m1-host-validation/runtime/{cellId}/{vectorId}",
                    mustExist: false);
                FixtureRecipeRegistry.Prepare(Root, repositoryRoot, cellId, vector);
                var fixture = FrozenFixtureRegistry.MaterializePrepared(
                    Root,
                    cellId,
                    vector);
                var assetsPath = Path.Join(repositoryRoot, "obj", "project.assets.json");
                var shouldHaveAssets = vectorId is not (
                    "toolchain.missing-assets" or "toolchain.no-automatic-restore");
                Assert.Equal(shouldHaveAssets, File.Exists(assetsPath));
                Assert.Equal(
                    shouldHaveAssets,
                    fixture.ArrangementInputs.Any(input =>
                        input.Path.EndsWith("/obj/project.assets.json", StringComparison.Ordinal)));
                if (shouldHaveAssets)
                {
                    Assert.NotEmpty(File.ReadAllBytes(assetsPath));
                }

                FixtureRecipeRegistry.Provision(Root, repositoryRoot, cellId, vector);
                var firstIdentity = CellExecutor.ComputeRepositoryIdentity(
                    RepositoryObserver.Capture(repositoryRoot, fixture.AllowedDesignTimeRoots));
                Assert.Equal(fixture.RepositoryIdentitySha256, firstIdentity);
                Directory.CreateDirectory(Path.Join(repositoryRoot, "obj"));
                File.WriteAllText(
                    Path.Join(repositoryRoot, "obj", "inherited-run-state.txt"),
                    "must be removed",
                    new UTF8Encoding(false));
                FixtureRecipeRegistry.Provision(Root, repositoryRoot, cellId, vector);
                Assert.False(File.Exists(Path.Join(
                    repositoryRoot,
                    "obj",
                    "inherited-run-state.txt")));
                Assert.Equal(
                    firstIdentity,
                    CellExecutor.ComputeRepositoryIdentity(
                        RepositoryObserver.Capture(
                            repositoryRoot,
                            fixture.AllowedDesignTimeRoots)));
            }
        }
        finally
        {
            foreach (var vectorId in vectorIds)
            {
                DeletePreparedFixture(cellId, vectorId);
            }
        }
    }

    [Fact]
    public void HostValidation_NetworkRecorderRequiresSubjectActivationHandshake()
    {
        var context = BundleValidator.Validate(Root);
        var activationRecord =
            context.NetworkEvidenceProfile.RecorderActivationRecord;
        var temp = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-hv-network-recorder-{Guid.NewGuid():N}.jsonl");
        try
        {
            Assert.Equal(
                "missing",
                CellExecutor.ObserveNetworkOperationLog(temp, activationRecord));
            File.WriteAllText(temp, string.Empty, new UTF8Encoding(false));
            Assert.Equal(
                "missing",
                CellExecutor.ObserveNetworkOperationLog(temp, activationRecord));
            File.WriteAllText(temp, "{\"state\":\"active\"}\n", new UTF8Encoding(false));
            Assert.Equal(
                "missing",
                CellExecutor.ObserveNetworkOperationLog(temp, activationRecord));
            File.WriteAllText(temp, $"{activationRecord}\n", new UTF8Encoding(false));
            Assert.Equal(
                "empty",
                CellExecutor.ObserveNetworkOperationLog(temp, activationRecord));
            File.AppendAllText(
                temp,
                "{\"operation\":\"provider\"}\n",
                new UTF8Encoding(false));
            Assert.Equal(
                "operation-observed",
                CellExecutor.ObserveNetworkOperationLog(temp, activationRecord));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void HostValidation_NetworkEvidenceIsRecomputedBeforeAcceptance()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var cell = subject.Cells[0];
        var vector = context.Vectors.Vectors.Single(item =>
            item.VectorId == "network.no-contractscribe-initiated-operation");
        var fixture = cell.Fixtures.Single(item => item.VectorId == vector.VectorId) with
        {
            CapabilityAvailable = true,
            BlockedReasonCode = null
        };
        var repositoryDelta = new RepositoryDelta([], [], [], [], [], [], [], [], []);
        var materialization = cell.Materialization with
        {
            SelectedRuntime = Environment.Version.ToString()
        };
        var actualEvidence = NetworkEvidenceEvaluator.Evaluate(
            context,
            subject.SourceConfiguration,
            materialization,
            "operation-observed",
            true,
            [],
            repositoryDelta);
        var fabricatedCleanEvidence = actualEvidence with
        {
            Methods = actualEvidence.Methods.Select(method => method with
            {
                Status = "complete",
                ObservationCode = $"network.synthetic-{method.MethodId}-clean",
                CauseClass = null
            }).ToArray()
        };
        var process = new ProcessObservation(
            1,
            "started",
            "crash",
            false,
            true,
            true,
            "process-observation",
            "observe",
            true,
            "observed",
            NetworkOperationRecorderState: "operation-observed",
            NetworkEvidence: fabricatedCleanEvidence);
        var run = new RunEvidence(
            vector.VectorId,
            "run-1",
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            null,
            process,
            null,
            null,
            repositoryDelta,
            [],
            []);

        var derived = RunSemantics.Derive(
            context,
            vector,
            run,
            fixture,
            subject.SourceConfiguration,
            materialization);

        Assert.Equal("protocol-invalid-observation", derived.Verdict);
        Assert.Equal("network.evidence-observation-mismatch", derived.Observation);
        Assert.Equal(["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"], derived.DiagnosticCodes);
    }

    [Fact]
    public void HostValidation_NetworkClaimSchemasFailClosed()
    {
        _ = BundleValidator.Validate(Root);
        foreach (var schemaName in new[]
                 {
                     "m1-host-validation-cell-evidence-v1.schema.json",
                     "m1-host-validation-aggregate-evidence-v1.schema.json",
                     "m1-host-validation-incomplete-evidence-v1.schema.json",
                     "m1-host-validation-publication-record-v1.schema.json"
                 })
        {
            var schema = JsonNode.Parse(File.ReadAllText(
                Path.Join(Root, "schemas", "validation", schemaName)))!.AsObject();
            Assert.Contains(
                schema["required"]!.AsArray(),
                item => item!.GetValue<string>() == "networkClaimSetId");
            Assert.Equal(
                NetworkClaimSetRegistry.ClaimSetId,
                schema["properties"]!["networkClaimSetId"]!["const"]!.GetValue<string>());
            Assert.True(schema["additionalProperties"]!.GetValue<bool>() is false);
        }

        var temp = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-hv-network-claim-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var protocolPath = Path.Join(FixtureRoot, "protocol.json");
            var protocolSchema = Path.Join(
                Root,
                "schemas",
                "validation",
                "m1-host-validation-protocol-v1.schema.json");
            var protocol = JsonNode.Parse(File.ReadAllText(protocolPath))!.AsObject();
            var mutations = new Action<JsonObject>[]
            {
                root => root["publicSafety"]!.AsObject().Remove("networkClaimSetId"),
                root => root["publicSafety"]!["networkClaimSetId"] = "m1.synthetic-claim-set.v1",
                root => root["publicSafety"]!["networkClaimSetMembers"]!.AsArray().RemoveAt(0),
                root =>
                {
                    var members = root["publicSafety"]!["networkClaimSetMembers"]!.AsArray();
                    members.Add(members[0]!.DeepClone());
                },
                root => root["publicSafety"]!["networkClaimSetMembers"]!.AsArray().Add(
                    new JsonObject
                    {
                        ["claimId"] = "m1.synthetic-additional-claim.v1",
                        ["text"] = "Synthetic additional claim."
                    }),
                root =>
                {
                    var members = root["publicSafety"]!["networkClaimSetMembers"]!.AsArray();
                    var first = members[0]!.DeepClone();
                    members[0] = members[1]!.DeepClone();
                    members[1] = first;
                },
                root => root["publicSafety"]!["networkClaimSetMembers"]![0]!["text"] =
                    "The runtime is disconnected from the Internet.",
                root => root["publicSafety"]!.AsObject()["networkClaimProse"] =
                    "The runtime is disconnected from the Internet."
            };
            foreach (var (mutation, index) in mutations.Select((item, index) => (item, index)))
            {
                var mutated = protocol.DeepClone().AsObject();
                mutation(mutated);
                var path = Path.Join(temp, $"protocol-mutation-{index}.json");
                File.WriteAllText(path, mutated.ToJsonString(), new UTF8Encoding(false));
                Assert.Equal(
                    "HV111_SCHEMA_REJECTED",
                    Assert.Throws<ProtocolException>(() =>
                        SchemaValidation.Validate(path, protocolSchema)).Code);
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_TransitiveManagedNetworkDependencyCannotBeOmitted()
    {
        var temp = Path.Join(Root, "TestResults", $"host-validation-network-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
            var helperDirectory = Path.Join(temp, "dependency");
            var entryDirectory = Path.Join(temp, "entry");
            Directory.CreateDirectory(helperDirectory);
            Directory.CreateDirectory(entryDirectory);
            var helperPath = Path.Join(helperDirectory, "Acme.Networking.dll");
            EmitAssembly(
                helperPath,
                "Acme.Networking",
                "using System.Net.Http; public static class Client { public static object Send() => new HttpClient(); }",
                references);
            var entryPath = Path.Join(entryDirectory, "Production.Entry.dll");
            EmitAssembly(
                entryPath,
                "Production.Entry",
                "public static class Entry { public static object Run() => Client.Send(); }",
                references.Append(MetadataReference.CreateFromFile(helperPath)));

            var entryIdentity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, entryPath),
                CanonicalJson.Sha256File(entryPath));
            var helperIdentity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, helperPath),
                CanonicalJson.Sha256File(helperPath));
            var source = new SubjectSourceConfiguration(
                $"source.{new string('1', 64)}",
                new string('2', 40),
                $"operations.{new string('3', 64)}",
                [],
                [],
                entryIdentity,
                entryIdentity,
                entryIdentity,
                entryIdentity,
                entryIdentity,
                entryIdentity,
                entryIdentity);
            var materialization = new CellMaterialization(
                "ubuntu-x64",
                "job",
                "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
                "image",
                "linux-x64",
                "x64",
                "10.0.102",
                Environment.Version.ToString(),
                "18.0.0",
                [entryIdentity],
                [],
                []);
            Assert.Equal(
                "HV244_PRODUCTION_DEPENDENCY_CLOSURE",
                Assert.Throws<ProtocolException>(() =>
                    NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                        Root,
                        source,
                        materialization)).Code);
            Assert.False(NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                Root,
                source,
                materialization with { RuntimeDependencies = [helperIdentity] }));

            var collisionAssemblyName = "System.Console";
            var collisionReferences = references
                .Where(reference => !string.Equals(
                    Path.GetFileNameWithoutExtension(reference.FilePath),
                    collisionAssemblyName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var collisionHelperPath = Path.Join(
                helperDirectory,
                $"{collisionAssemblyName}.dll");
            EmitAssembly(
                collisionHelperPath,
                collisionAssemblyName,
                """
                using System.Net.Http;
                using System.Reflection;
                [assembly: AssemblyVersion("99.0.0.0")]
                public static class RuntimeNameCollisionClient
                {
                    public static object Send() => new HttpClient();
                }
                """,
                collisionReferences);
            var collisionEntryPath = Path.Join(
                entryDirectory,
                "Production.Collision.Entry.dll");
            EmitAssembly(
                collisionEntryPath,
                "Production.Collision.Entry",
                """
                public static class CollisionEntry
                {
                    public static object Run() => RuntimeNameCollisionClient.Send();
                }
                """,
                collisionReferences.Append(
                    MetadataReference.CreateFromFile(collisionHelperPath)));
            var collisionEntryIdentity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, collisionEntryPath),
                CanonicalJson.Sha256File(collisionEntryPath));
            var collisionHelperIdentity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, collisionHelperPath),
                CanonicalJson.Sha256File(collisionHelperPath));
            Assert.Equal(
                "HV244_PRODUCTION_DEPENDENCY_CLOSURE",
                Assert.Throws<ProtocolException>(() =>
                    NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                        Root,
                        source,
                        materialization with
                        {
                            SelectedRuntime = Environment.Version.ToString(),
                            ProductionArtifacts = [collisionEntryIdentity],
                            RuntimeDependencies = []
                        })).Code);
            Assert.True(
                NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                    Root,
                    source,
                    materialization with
                    {
                        SelectedRuntime = Environment.Version.ToString(),
                        ProductionArtifacts = [collisionEntryIdentity, collisionHelperIdentity],
                        RuntimeDependencies = []
                    }));

            foreach (var (assemblyName, sourceText) in new[]
                     {
                         (
                             "Acme.ReflectionBypass",
                             """
                             using System;
                             public static class ReflectionBypass
                             {
                                 public static object? Run()
                                 {
                                     var name = "System.Net.Http." + "HttpClient, System.Net.Http";
                                     var type = Type.GetType(name);
                                     return Activator.CreateInstance(type!);
                                 }
                             }
                             """),
                         (
                             "Acme.DynamicLoadBypass",
                             """
                             using System.Reflection;
                             public static class DynamicLoadBypass
                             {
                                 public static Assembly Run(string path) => Assembly.LoadFrom(path);
                             }
                             """),
                         (
                             "Acme.NativeBypass",
                             """
                             using System.Runtime.InteropServices;
                             public static class NativeBypass
                             {
                                 [DllImport("synthetic-native")]
                                 public static extern int Connect();
                             }
                             """)
                     })
            {
                var bypassPath = Path.Join(temp, $"{assemblyName}.dll");
                EmitAssembly(
                    bypassPath,
                    assemblyName,
                    sourceText,
                    references);
                var bypassIdentity = new ArtifactIdentity(
                    RepositoryPaths.ToRepositoryRelative(Root, bypassPath),
                    CanonicalJson.Sha256File(bypassPath));
                Assert.True(
                    NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                        Root,
                        source,
                        materialization with
                        {
                            ProductionArtifacts = [bypassIdentity],
                            RuntimeDependencies = []
                        }));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_TransitionAndLoaderFactsDriveTheOracle()
    {
        var context = BundleValidator.Validate(Root);
        var (manifest, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var temp = Path.Join(Root, "TestResults", $"host-validation-facts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var registryPath = Path.Join(temp, "host-failures.json");
            CanonicalJson.WriteCanonical(
                registryPath,
                new
                {
                    entries = new[]
                    {
                        new
                        {
                            code = "host.cancelled",
                            executionOutcome = "cancelled",
                            stage = "audit",
                            terminalState = "committed-non-success"
                        },
                        new
                        {
                            code = "host.multi-targeting-unsupported",
                            executionOutcome = "load-failure",
                            stage = "workspace-load",
                            terminalState = "committed-non-success"
                        }
                    },
                    formatVersion = "contractscribe-host-failure-registry-v1",
                    registryVersion = 1
                });
            var registry = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, registryPath),
                CanonicalJson.Sha256File(registryPath));
            var source = manifest.SourceConfiguration with { FailureRegistry = registry };
            var materialization = manifest.Cells[0].Materialization;

            var multi = context.Vectors.Vectors.Single(vector =>
                vector.VectorId == "support.multi-targeting");
            var multiFixture = manifest.Cells[0].Fixtures.Single(item =>
                item.VectorId == multi.VectorId) with
            {
                CapabilityAvailable = true,
                BlockedReasonCode = null
            };
            var multiFacts = new HostObservationFacts(
                source.SourceConfigurationId,
                source.HostRevision,
                source.ContractBaseline.Sha256,
                registry.Sha256,
                source.CalibratedBounds.Sha256,
                materialization.SelectedSdk,
                materialization.SelectedRuntime,
                materialization.SelectedMsbuild,
                [new NormalizedDiagnosticFact(
                    "host.multi-targeting-unsupported",
                    "workspace-load")],
                new OutputCommitFact("not-committed", null),
                [],
                new LoaderObservationFact(
                    "loader.unsupported.multi-targeting",
                    "whole-input-rejected",
                    false,
                    false));
            var multiResponse = new SubjectResponse(
                "contractscribe-m1-host-validation-subject-response-v1",
                multi.VectorId,
                "run-1",
                "started",
                "normal",
                null,
                "load-failure",
                registry.Sha256,
                "host.multi-targeting-unsupported",
                "workspace-load",
                "committed",
                "absent",
                multi.ExpectedEnforcementClass,
                "untrusted-subject-claim",
                null,
                multiFacts);
            var multiRun = new RunEvidence(
                multi.VectorId,
                "run-1",
                "matched",
                multi.ExpectedObservation,
                "unvalidated",
                multi.ExpectedEnforcementClass,
                multi.ExpectedEnforcementClass,
                multiResponse,
                new ProcessObservation(0, "started", "normal", false, true, true),
                null,
                null,
                new RepositoryDelta([], [], [], [], [], [], [], [], []),
                [new ObservedProcess(1, 0, "subject-runtime", "dotnet")],
                []);
            Assert.Equal(
                multi.ExpectedObservation,
                RunSemantics.Derive(
                    context,
                    multi,
                    multiRun,
                    multiFixture,
                    source,
                    materialization).Observation);

            var transition = context.Vectors.Vectors.Single(vector =>
                vector.VectorId == "cancellation.terminal-precedence");
            var transitionFixture = manifest.Cells[0].Fixtures.Single(item =>
                item.VectorId == transition.VectorId) with
            {
                CapabilityAvailable = true,
                BlockedReasonCode = null
            };
            var transitionFacts = multiFacts with
            {
                NormalizedDiagnosticFacts =
                [
                    new NormalizedDiagnosticFact("host.cancelled", "audit")
                ],
                LoaderFact = null
            };
            var transitionResponse = multiResponse with
            {
                VectorId = transition.VectorId,
                ProcessTermination = "crash",
                ExecutionOutcome = "cancelled",
                FailureCode = "host.cancelled",
                FailureStage = "audit",
                HostFacts = transitionFacts
            };
            var transitionRun = multiRun with
            {
                VectorId = transition.VectorId,
                ExpectedObservation = transition.ExpectedObservation,
                ExpectedEnforcementClass = transition.ExpectedEnforcementClass,
                Subject = transitionResponse,
                Process = new ProcessObservation(
                    1,
                    "started",
                    "crash",
                    false,
                    true,
                    true,
                    "before-commit",
                    "cancel",
                    false,
                    "requested",
                    null,
                    [
                        "terminal-commit-cancelled",
                        "competing-terminal-attempt-rejected"
                    ])
            };
            Assert.Equal(
                transition.ExpectedObservation,
                RunSemantics.Derive(
                    context,
                    transition,
                    transitionRun,
                    transitionFixture,
                    source,
                    materialization).Observation);
            Assert.True(RunSemantics.HasExactTransitionTrace(
                transition.VectorId,
                [
                    "terminal-commit-cancelled",
                    "competing-terminal-attempt-rejected"
                ]));
            Assert.False(RunSemantics.HasExactTransitionTrace(
                transition.VectorId,
                [
                    "terminal-commit-cancelled",
                    "terminal-commit-cancelled",
                    "competing-terminal-attempt-rejected"
                ]));
            Assert.False(RunSemantics.HasExactTransitionTrace(
                transition.VectorId,
                [
                    "competing-terminal-attempt-rejected",
                    "terminal-commit-cancelled"
                ]));
            Assert.False(RunSemantics.HasExactTransitionTrace(
                "publication.same-directory-atomic",
                [
                    "atomic-rename-committed",
                    "staging-created-in-destination"
                ]));
            Assert.False(RunSemantics.HasExactTransitionTrace(
                "publication.same-directory-atomic",
                [
                    "staging-created-in-destination",
                    "atomic-rename-committed",
                    "late-terminal-attempt-rejected"
                ]));
            var reorderedLog = Path.Join(temp, "reordered-transition.jsonl");
            File.WriteAllText(
                reorderedLog,
                """
                {"sequence":2,"event":"competing-terminal-attempt-rejected"}
                {"sequence":1,"event":"terminal-commit-cancelled"}

                """,
                new UTF8Encoding(false));
            Assert.Equal(
                "HV245_TRANSITION_LOG_INVALID",
                Assert.Throws<ProtocolException>(() =>
                    CellExecutor.ObserveTransitionLog(reorderedLog)).Code);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_BoundMeasurementsComeFromHarnessObservedSurfaces()
    {
        var diagnostics = new[]
        {
            new NormalizedDiagnosticFact("host.synthetic", "audit")
        };
        Assert.Equal(
            CanonicalJson.SerializeCanonical(diagnostics).LongLength,
            RunSemantics.MeasureCanonicalDiagnosticBytes(diagnostics));

        var run = new RunEvidence(
            "bounds.temporary-disk",
            "run-1",
            "matched",
            "bounds.temporary-disk-within-limit",
            "bounds.temporary-disk-within-limit",
            "internally-enforceable",
            "internally-enforceable",
            null,
            new ProcessObservation(
                0,
                "started",
                "normal",
                false,
                true,
                true,
                TemporaryDiskHighWater: new TemporaryDiskHighWaterEvidence(
                    "peak-concurrent-logical-file-bytes",
                    "contractscribe-temporary-work-and-output-staging.v1",
                    "pre-subject-to-temporary-disk-high-water.v1",
                    31,
                    11,
                    42,
                    true,
                    false)),
            null,
            null,
            new RepositoryDelta([], [], [], [], [], [], [], [], [],
                AllowedDesignTimeCreatedOrChangedBytes: 11),
            [],
            []);
        Assert.Equal(42, RunSemantics.MeasureTemporaryDiskBytes(run));
        Assert.Equal(
            "HV239_MEASURED_BOUND_FACTS",
            Assert.Throws<ProtocolException>(() =>
                RunSemantics.MeasureTemporaryDiskBytes(
                    run with
                    {
                        Process = run.Process with
                        {
                            TemporaryDiskHighWater = null
                        }
                    })).Code);
    }

    [Fact]
    public void HostValidation_TemporaryDiskRetentionAndObserverFailuresCannotPass()
    {
        var context = BundleValidator.Validate(Root);
        var (subject, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var cell = subject.Cells[0];
        var vector = context.Vectors.Vectors.Single(item =>
            item.VectorId == "bounds.temporary-disk");
        var fixture = cell.Fixtures.Single(item => item.VectorId == vector.VectorId) with
        {
            CapabilityAvailable = true,
            BlockedReasonCode = null
        };
        var process = new ProcessObservation(
            0,
            "started",
            "normal",
            false,
            true,
            true,
            "temporary-disk-high-water",
            "measure-temporary-disk",
            false,
            "observed",
            TemporaryDiskHighWater: new TemporaryDiskHighWaterEvidence(
                "peak-concurrent-logical-file-bytes",
                "contractscribe-temporary-work-and-output-staging.v1",
                "pre-subject-to-temporary-disk-high-water.v1",
                8192,
                0,
                8192,
                true,
                false));
        var run = new RunEvidence(
            vector.VectorId,
            "run-1",
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            null,
            process,
            null,
            null,
            new RepositoryDelta([], [], [], [], [], [], [], [], []),
            [],
            []);

        var retention = RunSemantics.Derive(
            context,
            vector,
            run with
            {
                Process = process with
                {
                    TemporaryDiskHighWater =
                        process.TemporaryDiskHighWater! with { RetentionBreach = true }
                }
            },
            fixture,
            subject.SourceConfiguration,
            cell.Materialization);
        Assert.Equal("subject-nonconformance", retention.Verdict);
        Assert.Equal("bounds.temporary-disk-retention-breach", retention.Observation);

        var incomplete = RunSemantics.Derive(
            context,
            vector,
            run with
            {
                Process = process with
                {
                    TemporaryDiskHighWater =
                        process.TemporaryDiskHighWater! with { ObserverComplete = false }
                }
            },
            fixture,
            subject.SourceConfiguration,
            cell.Materialization);
        Assert.Equal("vector-infrastructure-incomplete", incomplete.Verdict);
        Assert.Equal("bounds.temporary-disk-observer-incomplete", incomplete.Observation);

        var missingGate = RunSemantics.Derive(
            context,
            vector,
            run with
            {
                Process = process with
                {
                    ControlCompleted = false,
                    ObservedControlOutcome = "gate-timeout",
                    TemporaryDiskHighWater = null
                }
            },
            fixture,
            subject.SourceConfiguration,
            cell.Materialization);
        Assert.Equal("subject-nonconformance", missingGate.Verdict);
        Assert.Equal(
            "bounds.temporary-disk-retention-contract-missing",
            missingGate.Observation);

        var observerRoot = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-hv-observer-{Guid.NewGuid():N}");
        var temporaryRoot = Path.Join(observerRoot, "temporary");
        var stagingRoot = Path.Join(observerRoot, "staging");
        Directory.CreateDirectory(temporaryRoot);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var stalePath = Path.Join(stagingRoot, "stale.json");
            File.WriteAllText(stalePath, "stale", new UTF8Encoding(false));
            Assert.Equal(
                "HV242_TEMPORARY_DISK_CONTRACT",
                Assert.Throws<ProtocolException>(() =>
                    new TemporaryDiskHighWaterObserver(
                        temporaryRoot,
                        stagingRoot)).Code);
            File.Delete(stalePath);

            using var observer = new TemporaryDiskHighWaterObserver(
                temporaryRoot,
                stagingRoot);
            var shrinkingPath = Path.Join(temporaryRoot, "shrinking.bin");
            File.WriteAllBytes(shrinkingPath, new byte[8 * 1024]);
            observer.Synchronize();
            using (var stream = new FileStream(
                       shrinkingPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(1024);
                stream.Flush(flushToDisk: true);
            }
            var gate = observer.GateContract;
            WriteBoundarySentinels(gate, freeze: true);
            var shrinkEvidence = observer.CaptureAndRelease(() =>
                WriteBoundarySentinels(gate, freeze: false),
                MonotonicDeadline.Start(TimeSpan.FromSeconds(10)));
            Assert.True(shrinkEvidence.ObserverComplete);
            Assert.True(shrinkEvidence.RetentionBreach);
            Assert.Equal(8 * 1024, shrinkEvidence.TotalBytes);
        }
        finally
        {
            Directory.Delete(observerRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("grow")]
    [InlineData("equal-length-rewrite")]
    [InlineData("capture-release-window")]
    public void HostValidation_TemporaryDiskFreezeBoundaryRejectsPostGateMutation(
        string mutation)
    {
        var observerRoot = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-hv-freeze-{Guid.NewGuid():N}");
        var temporaryRoot = Path.Join(observerRoot, "temporary");
        var stagingRoot = Path.Join(observerRoot, "staging");
        Directory.CreateDirectory(temporaryRoot);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var observer = new TemporaryDiskHighWaterObserver(
                temporaryRoot,
                stagingRoot);
            var subjectPath = Path.Join(temporaryRoot, "subject.bin");
            if (mutation != "create")
            {
                File.WriteAllBytes(subjectPath, [1, 2, 3, 4]);
                observer.Synchronize();
            }

            var gate = observer.GateContract;
            WriteBoundarySentinels(gate, freeze: true);
            switch (mutation)
            {
                case "create":
                    File.WriteAllBytes(subjectPath, [1, 2, 3, 4]);
                    break;
                case "grow":
                    using (var stream = new FileStream(
                               subjectPath,
                               FileMode.Append,
                               FileAccess.Write,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Write([5, 6, 7, 8]);
                        stream.Flush(flushToDisk: true);
                    }
                    break;
                case "equal-length-rewrite":
                    File.WriteAllBytes(subjectPath, [4, 3, 2, 1]);
                    break;
            }

            var evidence = observer.CaptureAndRelease(() =>
            {
                if (mutation == "capture-release-window")
                {
                    File.WriteAllBytes(subjectPath, [4, 3, 2, 1]);
                }
                WriteBoundarySentinels(gate, freeze: false);
            }, MonotonicDeadline.Start(TimeSpan.FromSeconds(10)));
            Assert.True(evidence.ObserverComplete);
            Assert.True(evidence.RetentionBreach);
        }
        finally
        {
            Directory.Delete(observerRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostValidation_SystemTemporaryWritesAreRedirectedIntoTheAuditRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-hv-temp-observation-{Guid.NewGuid():N}");
        var auditRoot = Path.Join(root, "audit");
        Directory.CreateDirectory(auditRoot);
        try
        {
            var requestPath = Path.Join(root, "request.json");
            var responsePath = Path.Join(root, "response.json");
            CanonicalJson.WriteCanonical(
                requestPath,
                new SubjectRequest(
                    "contractscribe-m1-host-validation-subject-request-v1",
                    "self-test",
                    "self-test.temporary-over-limit",
                    "run-1",
                    root,
                    responsePath,
                    null,
                    [],
                    "continue",
                    null,
                    null,
                    auditRoot));
            var before = RepositoryObserver.Capture(auditRoot);
            var execution = await SubjectProcessRunner.RunAsync(
                "dotnet",
                [
                    typeof(BundleValidator).Assembly.Location,
                    "fake-subject",
                    "--request",
                    requestPath,
                    "--behavior",
                    "temporary-final-write"
                ],
                root,
                16 * 1024,
                16 * 1024,
                TimeSpan.FromSeconds(10),
                auditTemporaryRoot: auditRoot);
            var delta = RepositoryObserver.Compare(
                before,
                RepositoryObserver.Capture(auditRoot));
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(8 * 1024, delta.OtherCreatedOrChangedBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_SubjectReportedLowerBoundsCannotOverrideHarnessMeasurements()
    {
        var temp = Path.Join(
            Root,
            "TestResults",
            $"host-validation-bound-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var boundsPath = Path.Join(temp, "bounds.json");
            CanonicalJson.WriteCanonical(
                boundsPath,
                new
                {
                    entries = new[]
                    {
                        new
                        {
                            name = "diagnostic-count",
                            unit = "count",
                            limit = 100L,
                            enforcementClass = "internally-enforceable"
                        },
                        new
                        {
                            name = "diagnostic-utf8-bytes",
                            unit = "bytes",
                            limit = 65536L,
                            enforcementClass = "internally-enforceable"
                        },
                        new
                        {
                            name = "temporary-disk-bytes",
                            unit = "bytes",
                            limit = 1048576L,
                            enforcementClass = "internally-enforceable"
                        }
                    },
                    formatVersion = "contractscribe-m1-host-calibrated-bounds-v1"
                });
            var identity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, boundsPath),
                CanonicalJson.Sha256File(boundsPath));
            var source = new SubjectSourceConfiguration(
                $"source.{new string('1', 64)}",
                new string('2', 40),
                $"operations.{new string('3', 64)}",
                [],
                [],
                identity,
                identity,
                identity,
                identity,
                identity,
                identity,
                identity);
            var facts = new HostObservationFacts(
                source.SourceConfigurationId,
                source.HostRevision,
                identity.Sha256,
                identity.Sha256,
                identity.Sha256,
                "10.0.102",
                "10.0.0",
                "18.0.0",
                [new NormalizedDiagnosticFact("host.synthetic", "audit")],
                new OutputCommitFact("not-committed", null),
                [
                    new MeasuredBoundFact(
                        "diagnostic-count",
                        "count",
                        0,
                        100,
                        "internally-enforceable"),
                    new MeasuredBoundFact(
                        "diagnostic-utf8-bytes",
                        "bytes",
                        0,
                        65536,
                        "internally-enforceable")
                ]);
            Assert.Equal(
                "HV239_MEASURED_BOUND_FACTS",
                Assert.Throws<ProtocolException>(() =>
                    RunSemantics.ValidateMeasuredBounds(
                        Root,
                        source,
                        facts,
                        new Dictionary<string, long>(StringComparer.Ordinal)
                        {
                            ["diagnostic-count"] = 1,
                            ["diagnostic-utf8-bytes"] =
                                RunSemantics.MeasureCanonicalDiagnosticBytes(
                                    facts.NormalizedDiagnosticFacts)
                        })).Code);
            Assert.Equal(
                "HV239_MEASURED_BOUND_FACTS",
                Assert.Throws<ProtocolException>(() =>
                    RunSemantics.ValidateMeasuredBounds(
                        Root,
                        source,
                        facts with
                        {
                            MeasuredBounds =
                            [
                                new MeasuredBoundFact(
                                    "temporary-disk-bytes",
                                    "bytes",
                                    1,
                                    1048576,
                                    "internally-enforceable")
                            ]
                        },
                        new Dictionary<string, long>(StringComparer.Ordinal)
                        {
                            ["temporary-disk-bytes"] = 8192
                        })).Code);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_ProductionCommandContractRejectsPrefixesAndLiteralArguments()
    {
        var subjectSchema = Path.Join(
            Root,
            "schemas",
            "validation",
            "m1-host-validation-subject-v1.schema.json");
        var context = BundleValidator.Validate(Root);
        var (subject, _) = CreateSyntheticIncompleteCell(context, "ubuntu-x64");
        var template = JsonNode.Parse(Encoding.UTF8.GetString(CanonicalJson.SerializeCanonical(
            subject.CellManifest("ubuntu-x64"))))!.AsObject();
        template["subject"]!["argumentPrefix"] = new JsonArray("adaptable-command");
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
                [protocolIdentity, protocolIdentity],
                [],
                []),
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
            "absent",
            [new RunWorkingDirectory("run-1", "repository-root")],
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

        var optionalAbsentPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "optional-absent.json");
        AssertInvalidTaxonomyMutation(optionalAbsentPath, root =>
            root["results"]![0]!["classification"]!["parentSymbolRef"]!["documentationCommentId"]
                = "M:AuditFixtures.Widget.#ctor");
        AssertInvalidTaxonomyMutation(componentPath, root =>
            root["results"]![0]!["evidenceBundle"]!["omissionReason"]
                = "evidence.omission.source-unavailable");

        var policyAgreePath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "policy-agree.json");
        AssertInvalidTaxonomyMutation(policyAgreePath, root =>
        {
            var contributions = root["results"]![0]!["policyContributions"]!.AsArray();
            var first = contributions[0]!.DeepClone();
            contributions[0] = contributions[1]!.DeepClone();
            contributions[1] = first;
        });

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
        Assert.Equal(
            "HV210_SUBJECT_OUTCOME_ILLEGAL",
            Assert.Throws<ProtocolException>(() =>
                ValidateWithReplacement(
                    context,
                    subject,
                    evidence,
                    run with
                    {
                        Subject = run.Subject! with
                        {
                            ArtifactState = "published"
                        }
                    })).Code);
        Assert.Equal(
            "HV235_HOST_FACTS_MISSING",
            Assert.Throws<ProtocolException>(() =>
                ValidateWithReplacement(
                    context,
                    subject,
                    evidence,
                    run with
                    {
                        Subject = run.Subject! with { HostFacts = null }
                    })).Code);
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
        var entryPointSha256 = CanonicalJson.Sha256File(entryPoint);
        Assert.Equal(
            "contractscribe-worker",
            ProcessTreeObserver.ClassifyIdentity(
                "dotnet",
                entryPoint,
                ["build"],
                [new ProcessIdentityRule(fingerprint, "production-subject", entryPointSha256)]));
        Assert.Equal(
            "restore-or-runtime-download",
            ProcessTreeObserver.ClassifyIdentity(
                "dotnet",
                entryPoint,
                ["restore"],
                [new ProcessIdentityRule(fingerprint, "production-subject", entryPointSha256)]));
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
                [new ProcessIdentityRule(
                    restoreFingerprint,
                    "production-subject",
                    entryPointSha256)]));

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
                "unknown-descendant",
                ProcessTreeObserver.ClassifyIdentity(
                    "dotnet",
                    toolchainEntryPoint,
                    ["build"],
                    [new ProcessIdentityRule(
                        toolchainFingerprint,
                        "fixture-helper",
                        CanonicalJson.Sha256File(toolchainEntryPoint))]));
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
        var matched = CreateMatchedRun(
            context,
            subject,
            vector.VectorId,
            "run-1",
            702,
            '2');
        var baseline = matched with
        {
            Subject = matched.Subject! with
            {
                ObservationCode = "toolchain.missing-assets-classified",
                EnforcementClass = "internally-enforceable"
            },
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
    public void HostValidation_ProtectedBuildHostGrammarIsExactAndFailClosed()
    {
        var buildHostPath = Path.Join(
            Root,
            "src",
            "ContractScribe.Cli",
            "bin",
            "Release",
            "net10.0",
            "BuildHost-netcore",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll");
        Assert.True(File.Exists(buildHostPath));
        var relativePath = RepositoryPaths.ToRepositoryRelative(Root, buildHostPath);
        var identity = new ArtifactIdentity(relativePath, CanonicalJson.Sha256File(buildHostPath));
        var materialization = new CellMaterialization(
            OperatingSystem.IsWindows() ? "windows-x64" : "ubuntu-x64",
            "host-validation-cell",
            "https://github.com/SolusQuest/contract-scribe/actions/runs/1",
            "image",
            OperatingSystem.IsWindows() ? "win-x64" : "linux-x64",
            "X64",
            "sdk",
            Environment.Version.ToString(),
            "msbuild",
            [],
            [identity],
            []);
        var registry = SubjectManifestMaterializer.BuildHostProcessIdentityRegistry(
            materialization,
            required: true);
        var hostArguments = new[] { "--roll-forward", "LatestMajor" };
        var commandArguments = new List<string>
        {
            "--pipe", Guid.NewGuid().ToString("D"),
            "--property", "DesignTimeBuild=true",
            "--property", @"NonExistentFile=__NonExistentSubDir__\__NonExistentFile__",
            "--property", "BuildingInsideVisualStudio=true",
            "--property", "BuildProjectReferences=false",
            "--property", "BuildingProject=false",
            "--property", "ProvideCommandLineArgs=true",
            "--property", "SkipCompilerExecution=true",
            "--property", "ContinueOnError=ErrorAndContinue",
            "--property", "ShouldUnsetParentConfigurationAndPlatform=false",
            "--property", $"SolutionDir={Path.Join(Root, "tests", "fixtures", "m1-host-validation", "runtime", materialization.CellId, "support.sln")}{Path.DirectorySeparatorChar}",
            "--locale", "en-US"
        };
        Assert.Equal(
            "toolchain-owned",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                hostArguments,
                commandArguments,
                registry));

        Assert.Equal(
            "unknown-descendant",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                ["--roll-forward"],
                commandArguments,
                registry));
        Assert.Equal(
            "unknown-descendant",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                hostArguments,
                commandArguments.Select(argument =>
                    argument == commandArguments[1] ? "not-a-guid" : argument).ToArray(),
                registry));
        Assert.Equal(
            "unknown-descendant",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                hostArguments,
                commandArguments.Concat(["--property", "Unexpected=true"]).ToArray(),
                registry));
        Assert.Equal(
            "restore-or-runtime-download",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                hostArguments,
                commandArguments.Concat(["restore"]).ToArray(),
                registry));
        Assert.Equal(
            "unknown-descendant",
            ProcessTreeObserver.ClassifyDotnetIdentity(
                buildHostPath,
                hostArguments,
                commandArguments,
                [registry[0] with { EntryPointSha256 = new string('0', 64) }]));

        var copiedRoot = Path.Join(
            Root,
            "TestResults",
            "m1-host-validation",
            $"buildhost-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copiedRoot);
        var copied = Path.Join(copiedRoot, Path.GetFileName(buildHostPath));
        try
        {
            File.Copy(buildHostPath, copied);
            Assert.Equal(
                "unknown-descendant",
                ProcessTreeObserver.ClassifyDotnetIdentity(
                    copied,
                    hostArguments,
                    commandArguments,
                    registry));
        }
        finally
        {
            Directory.Delete(copiedRoot, recursive: true);
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
    public void HostValidation_ArtifactSetRequiresOneExactTerminalPerRequiredCell()
    {
        var context = BundleValidator.Validate(Root);
        var temp = Path.Join(
            Root,
            "TestResults",
            "m1-host-validation",
            "artifact-set-tests",
            Guid.NewGuid().ToString("N"),
            "root");
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(
                Path.Join(temp, SubjectManifestMaterializer.CommonFileName),
                "{}\n",
                new UTF8Encoding(false));
            foreach (var cell in context.Protocol.RequiredCells)
            {
                var directory = Path.Join(temp, cell.CellId);
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Join(directory, SubjectManifestMaterializer.CellFileName),
                    "{}\n",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Join(directory, "cell-evidence.json"),
                    "{}\n",
                    new UTF8Encoding(false));
            }

            var exact = HostValidationArtifactSet.Load(context, temp);
            Assert.Equal(2, exact.Cells.Count);
            var preserved = exact.InputPaths()
                .Where(File.Exists)
                .ToDictionary(path => path, File.ReadAllBytes);
            foreach (var collision in new[]
            {
                exact.CommonManifestPath,
                exact.Cells[0].CellManifestPath,
                exact.Cells[0].TerminalPath,
                exact.Cells[1].CellManifestPath,
                exact.Cells[1].TerminalPath,
                exact.Root,
                Directory.GetParent(exact.Root)!.FullName
            })
            {
                Assert.Equal(
                    "HV194_OUTPUT_PATH_COLLISION",
                    Assert.Throws<ProtocolException>(() =>
                        OutputPathGuard.Validate(
                            context,
                            exact.InputPaths(),
                            collision)).Code);
                Assert.All(preserved, pair =>
                    Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
            }

            var extra = Path.Join(temp, "unreferenced.json");
            File.WriteAllText(extra, "{}\n", new UTF8Encoding(false));
            AssertArtifactSetInvalid(context, temp);
            File.Delete(extra);

            var windowsTerminal = Path.Join(temp, "windows-x64", "cell-evidence.json");
            File.Delete(windowsTerminal);
            AssertArtifactSetInvalid(context, temp);
            File.WriteAllText(windowsTerminal, "{}\n", new UTF8Encoding(false));

            var duplicateTerminal = Path.Join(temp, "ubuntu-x64", "incomplete-evidence.json");
            File.WriteAllText(duplicateTerminal, "{}\n", new UTF8Encoding(false));
            AssertArtifactSetInvalid(context, temp);
            File.Delete(duplicateTerminal);

            var unexpectedCellFile = Path.Join(temp, "windows-x64", "ubuntu-x64.json");
            File.WriteAllText(unexpectedCellFile, "{}\n", new UTF8Encoding(false));
            AssertArtifactSetInvalid(context, temp);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HostValidation_WorkflowKeepsOrdinaryCiAndExplicitExecutionMutuallyExclusive()
    {
        var workflow = File.ReadAllText(Path.Join(Root, ".github", "workflows", "ci.yml"));

        Assert.Contains("mode == 'ordinary-ci'", workflow, StringComparison.Ordinal);
        Assert.Contains("mode == 'host-validation-execution'", workflow, StringComparison.Ordinal);
        Assert.Contains("expected_head_sha", workflow, StringComparison.Ordinal);
        Assert.Contains("host-validation-common:", workflow, StringComparison.Ordinal);
        Assert.Contains("host-validation-cell:", workflow, StringComparison.Ordinal);
        Assert.Contains("host-validation-aggregate:", workflow, StringComparison.Ordinal);
        Assert.Contains("materialize-common", workflow, StringComparison.Ordinal);
        Assert.Contains("materialize-cell", workflow, StringComparison.Ordinal);
        Assert.Contains("execute-cell", workflow, StringComparison.Ordinal);
        Assert.Contains("require-passing-aggregate", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--matrix-result", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("needs.host-validation-cell.result", workflow, StringComparison.Ordinal);
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
        Assert.Equal(30, cases.Length);
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
                [protocolIdentity, protocolIdentity],
                [],
                []),
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
            "absent",
            [new RunWorkingDirectory("run-1", "repository-root")],
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
            $"operations.{new string('2', 64)}",
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
                "absent",
                [new RunWorkingDirectory("run-1", "repository-root")],
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
        ValidateSyntheticCellSemantics(context, subject, evidence);

        var missing = evidence with { Runs = evidence.Runs.Skip(1).ToArray() };
        Assert.Equal(
            "HV154_EVIDENCE_EXECUTION_SET",
            Assert.Throws<ProtocolException>(() =>
                ValidateSyntheticCellSemantics(context, subject, missing)).Code);

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
                ValidateSyntheticCellSemantics(context, subject, falseMatch)).Code);

        var falseOutcome = evidence with { Outcome = "passed" };
        Assert.Equal(
            "HV213_FALSE_CELL_OUTCOME",
            Assert.Throws<ProtocolException>(() =>
                ValidateSyntheticCellSemantics(context, subject, falseOutcome)).Code);
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

    [Fact]
    public void HostValidation_ExecutableRejectsUnknownCommandOptions()
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
        start.ArgumentList.Add("validate-bundle");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(Root);
        start.ArgumentList.Add("--require-reveiw");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start the host-validation harness.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(
            process.WaitForExit(30_000),
            "Host-validation invalid-option test timed out.");

        Assert.Equal(2, process.ExitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal("HV004_OPTION_INVALID", stderr.Trim());
    }

    [Theory]
    [InlineData("materialize-common", "--source-revision")]
    [InlineData("materialize-cell", "--runner-image")]
    [InlineData("materialize-cell", "--fixture")]
    [InlineData("execute-cell", "--executable")]
    public void HostValidation_ExecutableRejectsCallerControlledMaterializationFacts(
        string command,
        string injectedOption)
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
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(injectedOption);
        start.ArgumentList.Add("synthetic");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the host-validation harness.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));

        Assert.Equal(2, process.ExitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal("HV004_OPTION_INVALID", stderr.Trim());
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
                ValidateSyntheticCellSemantics(
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
                ValidateSyntheticCellSemantics(context, subject, ubuntu with { Outcome = "passed" });
                return;
            case "remove-required-run":
                ValidateSyntheticCellSemantics(
                    context,
                    subject,
                    ubuntu with { Runs = ubuntu.Runs.Skip(1).ToArray() });
                return;
            case "duplicate-required-run":
                ValidateSyntheticCellSemantics(
                    context,
                    subject,
                    ubuntu with { Runs = ubuntu.Runs.Append(ubuntu.Runs[0]).ToArray() });
                return;
            case "replace-run-vector-with-unknown":
                ValidateSyntheticCellSemantics(
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
                    ValidateSyntheticCellSemantics(
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
                    ValidateSyntheticCellSemantics(
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
                        new CellAggregate(
                            "ubuntu-x64",
                            new string('1', 64),
                            "cell-evidence",
                            new string('a', 64),
                            ubuntu.Outcome),
                        new CellAggregate(
                            "windows-x64",
                            new string('2', 64),
                            "cell-evidence",
                            new string('b', 64),
                            windows.Outcome)
                    };
                    var aggregate = CreateSyntheticAggregate(ubuntu, expectedCells);
                    if (operation == "replace-cell-evidence-digest")
                    {
                        aggregate = aggregate with
                        {
                            Cells =
                            [
                                expectedCells[0] with { TerminalEvidenceSha256 = new string('c', 64) },
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
            case "replace-second-cell-source-configuration":
            case "replace-second-cell-common-manifest":
            case "replace-second-cell-review":
                {
                    var mixed = operation switch
                    {
                        "replace-second-cell-source-configuration" => windows with
                        {
                            SourceConfigurationId = $"source.{new string('3', 64)}"
                        },
                        "replace-second-cell-common-manifest" => windows with
                        {
                            CommonManifestSha256 = new string('4', 64)
                        },
                        _ => windows with { ReviewId = $"review.{new string('5', 64)}" }
                    };
                    EvidenceValidator.ValidateAggregateCellSemantics(context, [ubuntu, mixed]);
                    return;
                }
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
            case "replace-incomplete-common-manifest":
            case "replace-incomplete-cell-manifest":
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
                        NetworkClaimSetRegistry.ClaimSetId,
                        review.ReviewId,
                        subject.SourceConfiguration.SourceConfigurationId,
                        CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(subject.Common)),
                        CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(subject.CellManifest("ubuntu-x64"))),
                        subject.ValidationAttempt,
                        "ubuntu-x64",
                        "protocol-failure",
                        ["HV999_INTERNAL_ERROR"],
                        true);
                    incomplete = operation switch
                    {
                        "replace-review-id" => incomplete with
                        {
                            ReviewId = $"review.{new string('2', 64)}"
                        },
                        "replace-incomplete-common-manifest" => incomplete with
                        {
                            CommonManifestSha256 = new string('3', 64)
                        },
                        "replace-incomplete-cell-manifest" => incomplete with
                        {
                            CellManifestSha256 = new string('4', 64)
                        },
                        _ => incomplete with { Classification = "protected-input-invalidated" }
                    };
                    EvidenceValidator.ValidateIncompleteSemantics(
                        context,
                        review,
                        subject.Common,
                        subject.CellManifest("ubuntu-x64"),
                        incomplete);
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
        SyntheticSubject subject,
        CellEvidence evidence,
        RunEvidence replacement) =>
        ValidateSyntheticCellSemantics(
            context,
            EnableFixture(subject, evidence.Cell.CellId, replacement.VectorId),
            ReplaceRuns(evidence, replacement));

    private static void ValidateSyntheticCellSemantics(
        BundleContext context,
        SyntheticSubject subject,
        CellEvidence evidence) =>
        EvidenceValidator.ValidateCellSemantics(
            context,
            subject.Common,
            subject.CellManifest(evidence.Cell.CellId),
            evidence);

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

    private static SyntheticSubject EnableFixture(
        SyntheticSubject subject,
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
        SyntheticSubject subjectManifest,
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
        var isFailure = vectorId == "failure.invalid-input";
        var hasResult = !isFailure;
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
        var materialization = subjectManifest.Cells[0].Materialization;
        var hostFacts = new HostObservationFacts(
            subjectManifest.SourceConfiguration.SourceConfigurationId,
            subjectManifest.SourceConfiguration.HostRevision,
            subjectManifest.SourceConfiguration.ContractBaseline.Sha256,
            subjectManifest.SourceConfiguration.FailureRegistry.Sha256,
            subjectManifest.SourceConfiguration.CalibratedBounds.Sha256,
            materialization.SelectedSdk,
            materialization.SelectedRuntime,
            materialization.SelectedMsbuild,
            isFailure
                ? [new NormalizedDiagnosticFact("host.invalid-input", "input")]
                : [],
            new OutputCommitFact(
                isFailure ? "not-committed" : "committed",
                commitment?.Sha256),
            []);
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
            commitment,
            hostFacts);
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
            NetworkClaimSetRegistry.ClaimSetId,
            baseline.ReviewId,
            baseline.SourceConfigurationId,
            baseline.CommonManifestSha256,
            baseline.ValidationAttempt,
            new AggregateFinalizationIdentity(
                "incomplete",
                baseline.ValidationAttempt.ValidationExecutionSha),
            cells,
            "environment-or-infrastructure-incomplete");

    private static void AssertArtifactSetInvalid(BundleContext context, string path) =>
        Assert.Equal(
            "HV250_ARTIFACT_SET_INVALID",
            Assert.Throws<ProtocolException>(() =>
                HostValidationArtifactSet.Load(context, path)).Code);

    private static void AssertPendingReview(Action action) =>
        Assert.Equal(
            "HV121_REVIEW_NOT_ACCEPTED",
            Assert.Throws<ProtocolException>(action).Code);

    private static void AssertCanonicalPendingReview(ReviewRecord review)
    {
        Assert.Equal("pending", review.Verdict);
        Assert.Null(review.ReviewedSourceRevision);
        Assert.Null(review.ReviewerKind);
        Assert.Null(review.RelaySessionId);
        Assert.Null(review.RelayTaskId);
        Assert.Null(review.ReviewedAtUtc);
        Assert.Equal(["independent-review.pending"], review.BlockingFindingIds);
        Assert.Equal(BundleValidator.ComputeReviewId(review), review.ReviewId);
    }

    private static void AssertCanonicalAcceptedReview(ReviewRecord review)
    {
        Assert.Equal("accepted", review.Verdict);
        Assert.Matches("^[0-9a-f]{40}$", review.ReviewedSourceRevision!);
        Assert.Equal("independent-relay", review.ReviewerKind);
        Assert.True(Guid.TryParse(review.RelaySessionId, out var relaySessionId));
        Assert.NotEqual(Guid.Empty, relaySessionId);
        Assert.True(Guid.TryParse(review.RelayTaskId, out var relayTaskId));
        Assert.NotEqual(Guid.Empty, relayTaskId);
        Assert.NotNull(review.ReviewedAtUtc);
        Assert.Empty(review.BlockingFindingIds);
        Assert.Equal(BundleValidator.ComputeReviewId(review), review.ReviewId);
    }

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

    private static ReviewRecord CreateAcceptedReview(
        string bundleId,
        string reviewedSourceRevision) =>
        WithComputedReviewId(new ReviewRecord(
            "contractscribe-m1-host-validation-review-v1",
            string.Empty,
            bundleId,
            reviewedSourceRevision,
            "independent-relay",
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            "accepted",
            [],
            "2026-07-29T00:00:00Z"));

    private static ReviewRecord CreatePendingReviewRecord(string bundleId) =>
        WithComputedReviewId(new ReviewRecord(
            "contractscribe-m1-host-validation-review-v1",
            string.Empty,
            bundleId,
            null,
            null,
            null,
            null,
            "pending",
            ["independent-review.pending"],
            null));

    private static ReviewRecord WithComputedReviewId(ReviewRecord review) =>
        review with
        {
            ReviewId = BundleValidator.ComputeReviewId(review)
        };

    private static EnvironmentVariableScope GitHubEnvironment(string executionRevision) =>
        new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_REPOSITORY"] = "SolusQuest/contract-scribe",
            ["GITHUB_SERVER_URL"] = "https://github.com",
            ["GITHUB_WORKFLOW_REF"] =
                "SolusQuest/contract-scribe/.github/workflows/ci.yml@refs/heads/test",
            ["GITHUB_RUN_ID"] = "9001",
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["GITHUB_SHA"] = executionRevision,
            ["GITHUB_JOB"] = "host-validation-cell",
            ["RUNNER_OS"] = OperatingSystem.IsWindows() ? "Windows" : "Linux",
            ["RUNNER_ARCH"] = "X64",
            ["ImageOS"] = OperatingSystem.IsWindows() ? "win-test" : "ubuntu-test",
            ["ImageVersion"] = "20260807.1"
        });

    private static void DeletePreparedFixture(string cellId, string vectorId)
    {
        var repositoryRoot = RepositoryPaths.ResolveConfined(
            Root,
            $"tests/fixtures/m1-host-validation/runtime/{cellId}/{vectorId}",
            mustExist: false);
        if (Directory.Exists(repositoryRoot))
        {
            FixtureRecipeRegistry.RemoveProvisionedReparsePoints(repositoryRoot);
            Directory.Delete(repositoryRoot, recursive: true);
        }
        var preparedObj = FixtureRecipeRegistry.PreparedAssetRoot(Root, cellId, vectorId);
        var preparedVector = Directory.GetParent(preparedObj)?.FullName;
        if (preparedVector is not null && Directory.Exists(preparedVector))
        {
            Directory.Delete(preparedVector, recursive: true);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> original;

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            original = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in original)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
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

    private static (SyntheticSubject Subject, CellEvidence Evidence) CreateSyntheticIncompleteCell(
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
            $"operations.{new string('2', 64)}",
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
            [],
            [],
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
                "absent",
                vector.RunIds.Select(runId =>
                    new RunWorkingDirectory(runId, "repository-root")).ToArray(),
                null))
            .ToArray();
        var executionCell = new ExecutionCell(
            materialization,
            "dotnet-dll",
            "src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll",
            [],
            fixtures);
        var common = new CommonSourceManifest(
            "contractscribe-m1-host-validation-common-source-v1",
            context.Lock.BundleId,
            "production-host",
            "issue-24",
            "prebuilt-in-process-test-entrypoint",
            source,
            attempt);
        var subject = new SyntheticSubject(common, [executionCell]);
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
            NetworkClaimSetRegistry.ClaimSetId,
            $"review.{sha}",
            source.SourceConfigurationId,
            CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(common)),
            CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(subject.CellManifest(cellId))),
            attempt,
            materialization,
            runs.OrderBy(run => run.VectorId, StringComparer.Ordinal)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .ToArray(),
            "environment-or-infrastructure-incomplete");
        return (subject, evidence);
    }

    private static void WriteBoundarySentinels(
        TemporaryDiskGateContract contract,
        bool freeze)
    {
        var sentinel = freeze
            ? contract.FreezeSentinelName
            : contract.ReleaseSentinelName;
        foreach (var root in new[]
                 {
                     contract.TemporaryWorkRoot,
                     contract.OutputStagingRoot
                 })
        {
            using var stream = new FileStream(
                Path.Join(root, sentinel),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void EmitAssembly(
        string path,
        string assemblyName,
        string source,
        IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.ToString())));
    }
}
