using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
        Assert.Equal(
            "https://github.com/SolusQuest/contract-scribe/issues/55",
            context.Protocol.Baseline.CoordinatingIssue);
        Assert.Equal(
            "pending-main-reconciliation",
            context.Protocol.Baseline.Disposition);
        Assert.Null(context.Protocol.Baseline.MergeCommit);
        Assert.Equal(
            new PredecessorBaselineIdentity(
                "https://github.com/SolusQuest/contract-scribe/issues/35",
                "issue-35-pre-release-v1",
                "bb4654edc180e2953dda6b89a29211b18778b78e",
                "tests/fixtures/m1-contract-baseline/v1/manifest.json",
                "2872387ce9cfd8578c8f473ec26ab9f10dd44381edfbc0248e6fa370d797ab31"),
            context.Protocol.Baseline.Predecessor);
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
    public void HostValidation_PendingBaselineIsStructurallyClosedAndNonAuthorizing()
    {
        var context = BundleValidator.Validate(Root);
        var review = BundleValidator.ValidateReviewStructure(
            Root,
            BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        Assert.Equal("pending", review.Verdict);
        Assert.Null(review.ReviewedHead);
        Assert.Null(review.ReviewerKind);
        Assert.Null(review.RelaySessionId);
        Assert.Null(review.RelayTaskId);
        Assert.Null(review.ReviewedAtUtc);
        Assert.Equal(
            new[] { "baseline.main-reconciliation-pending" },
            review.BlockingFindingIds);
        Assert.Equal(
            "HV246_BASELINE_NOT_MAIN_REACHABLE",
            Assert.Throws<ProtocolException>(() =>
                BundleValidator.Validate(Root, requireReview: true)).Code);

        var tempRoot = Path.Join(
            Root,
            "TestResults",
            $"host-pending-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reviewPath = Path.Join(tempRoot, "review.json");
            var wrongBundle = review with
            {
                BundleId = $"m1hvp1.{new string('0', 64)}",
                ReviewId = string.Empty
            };
            wrongBundle = wrongBundle with
            {
                ReviewId = BundleValidator.ComputeReviewId(wrongBundle)
            };
            CanonicalJson.WriteCanonical(reviewPath, wrongBundle);
            Assert.Equal(
                "HV247_PENDING_REVIEW_INVALID",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.ValidateReviewStructure(
                        Root,
                        reviewPath,
                        context.Lock.BundleId)).Code);

            var mutatedId = review with
            {
                ReviewId = $"review.{new string('f', 64)}"
            };
            CanonicalJson.WriteCanonical(reviewPath, mutatedId);
            Assert.Equal(
                "HV166_REVIEW_ID_MISMATCH",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.ValidateReviewStructure(
                        Root,
                        reviewPath,
                        context.Lock.BundleId)).Code);

            var forgedAccepted = review with
            {
                ReviewedHead = new string('1', 40),
                ReviewerKind = "independent-relay",
                RelaySessionId = "00000000-0000-0000-0000-000000000001",
                RelayTaskId = "00000000-0000-0000-0000-000000000002",
                Verdict = "accepted",
                BlockingFindingIds = [],
                ReviewedAtUtc = "2026-07-29T00:00:00Z",
                ReviewId = string.Empty
            };
            forgedAccepted = forgedAccepted with
            {
                ReviewId = BundleValidator.ComputeReviewId(forgedAccepted)
            };
            CanonicalJson.WriteCanonical(reviewPath, forgedAccepted);
            Assert.Equal(
                "HV246_BASELINE_NOT_MAIN_REACHABLE",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.Validate(
                        Root,
                        requireReview: true,
                        reviewPath)).Code);

            var predecessorAccepted = new ReviewRecord(
                "contractscribe-m1-host-validation-review-v1",
                "review.5ac4ccffb8e481ebfa8f1b62cc10748125774201ba7f10e92f748266a753f863",
                "m1hvp1.5ed72dbb528120424ef36ff83334801adf6fcb5d33799874e4822094c71655b9",
                "0de7b3c51435f0dea69e8ca288c0fa6d8b0b0ca0",
                "independent-relay",
                "6a6769db-aab4-83ee-a41b-688ef866cfb9",
                "7be49341-b416-49e9-aa70-6042610af130",
                "accepted",
                [],
                "2026-07-29T05:10:53.554Z");
            Assert.Equal(
                predecessorAccepted.ReviewId,
                BundleValidator.ComputeReviewId(
                    predecessorAccepted with { ReviewId = string.Empty }));
            CanonicalJson.WriteCanonical(reviewPath, predecessorAccepted);
            Assert.Equal(
                "HV246_BASELINE_NOT_MAIN_REACHABLE",
                Assert.Throws<ProtocolException>(() =>
                    BundleValidator.Validate(
                        Root,
                        requireReview: true,
                        reviewPath)).Code);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostValidation_PendingCommandMatrixRejectsBeforeOutputCreation()
    {
        var tempRoot = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-host-pending-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var missing = Path.Join(tempRoot, "missing.json");
            var output = Path.Join(tempRoot, "output.json");
            var incomplete = Path.Join(tempRoot, "incomplete.json");
            var commands = new[]
            {
                new[] { "validate-bundle", "--root", Root, "--require-review" },
                new[] { "provision-fixtures", "--root", Root, "--subject-manifest", missing },
                new[] { "run-cell", "--root", Root, "--subject-manifest", missing, "--review", missing, "--cell", "ubuntu-x64", "--incomplete-output", incomplete, "--output", output },
                new[] { "validate-cell", "--root", Root, "--evidence", missing, "--review", missing, "--subject-manifest", missing },
                new[] { "validate-incomplete", "--root", Root, "--evidence", missing, "--review", missing, "--subject-manifest", missing },
                new[] { "aggregate", "--root", Root, "--evidence", missing, "--output", output, "--review", missing, "--subject-manifest", missing, "--matrix-result", "passed", "--publication-base-revision", new string('1', 40) },
                new[] { "validate-aggregate", "--root", Root, "--evidence", missing, "--cell-evidence", missing, "--review", missing, "--subject-manifest", missing },
                new[] { "validate-publication-record", "--root", Root, "--record", missing, "--aggregate-evidence", missing, "--cell-evidence", missing, "--review", missing, "--subject-manifest", missing },
                new[] { "prepare-public", "--root", Root, "--source", missing, "--output", output, "--review", missing, "--subject-manifest", missing, "--kind", "cell" }
            };

            foreach (var command in commands)
            {
                var result = await RunHostCommandAsync(command);
                Assert.Equal(2, result.ExitCode);
                Assert.Equal(string.Empty, result.Stdout);
                Assert.Equal(
                    "HV246_BASELINE_NOT_MAIN_REACHABLE\n",
                    result.Stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
                Assert.False(File.Exists(output));
                Assert.False(File.Exists(incomplete));
            }

            var isolatedRoot = Path.Join(tempRoot, "isolated-root");
            CopyHostBundleClosure(isolatedRoot);
            var protectedInputs = await RunHostCommandAsync(
                "lock-protected-inputs",
                "--root",
                isolatedRoot);
            Assert.Equal(0, protectedInputs.ExitCode);
            Assert.Matches(
                "^HV000_PROTECTED_INPUTS [1-9][0-9]*\\r?\\n$",
                protectedInputs.Stdout);
            Assert.Equal(string.Empty, protectedInputs.Stderr);

            var bundle = await RunHostCommandAsync(
                "lock-bundle",
                "--root",
                isolatedRoot);
            Assert.Equal(0, bundle.ExitCode);
            Assert.Matches(
                "^HV000_OK m1hvp1\\.[0-9a-f]{64}\\r?\\n$",
                bundle.Stdout);
            Assert.Equal(string.Empty, bundle.Stderr);

            var structural = await RunHostCommandAsync(
                "validate-bundle",
                "--root",
                isolatedRoot);
            Assert.True(
                structural.ExitCode == 0,
                $"Expected structural validation to succeed, but stderr was: {structural.Stderr}");
            Assert.Equal(bundle.Stdout, structural.Stdout);
            Assert.Equal(string.Empty, structural.Stderr);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
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

        var template = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
            Path.Join(FixtureRoot, "execution-subject.template.json"),
            4 * 1024 * 1024);
        foreach (var cell in template.Cells)
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
        var workingFixture = template.Cells[0].Fixtures.Single(fixture =>
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
        var subject = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
            Path.Join(FixtureRoot, "execution-subject.template.json"),
            4 * 1024 * 1024);
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
                [entryIdentity]);
            Assert.Equal(
                "HV244_PRODUCTION_DEPENDENCY_CLOSURE",
                Assert.Throws<ProtocolException>(() =>
                    NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                        Root,
                        source,
                        materialization)).Code);
            Assert.True(NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                Root,
                source,
                materialization with { BuiltArtifacts = [entryIdentity, helperIdentity] }));

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
                            BuiltArtifacts = [collisionEntryIdentity]
                        })).Code);
            Assert.True(
                NetworkOperationSourceScanner.HasContractScribeInitiatedNetworkOperation(
                    Root,
                    source,
                    materialization with
                    {
                        SelectedRuntime = Environment.Version.ToString(),
                        BuiltArtifacts =
                        [
                            collisionEntryIdentity,
                            collisionHelperIdentity
                        ]
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
                        materialization with { BuiltArtifacts = [bypassIdentity] }));
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
        var subject = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
            Path.Join(FixtureRoot, "execution-subject.template.json"),
            4 * 1024 * 1024);
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
    public void HostValidation_ConsumesClassificationOriginSkipMatrixAsFullDocuments()
    {
        using var matrix = CanonicalJson.ReadStrict(
            Path.Join(
                Root,
                "tests",
                "fixtures",
                "m1-contract-baseline",
                "v1",
                "classification-origin-skip-vectors.json"),
            2 * 1024 * 1024);
        foreach (var row in matrix.RootElement.GetProperty("cases").EnumerateArray())
        {
            using var document = BuildHostClassificationMatrixDocument(
                row.GetProperty("record"));
            var accepted = true;
            try
            {
                AuditResultSemanticValidator.Validate(
                    Root,
                    document.RootElement);
            }
            catch (ProtocolException exception)
                when (exception.Code == "HV230_AUDIT_RESULT_SEMANTICS")
            {
                accepted = false;
            }

            var expected = row.GetProperty("outcome").GetString() == "accept";
            Assert.True(
                expected == (accepted && HostConditionsSelectRecord(row)),
                row.GetProperty("caseId").GetString());
        }

        var correctedPath = Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            "unresolved-classification.json");
        using (var corrected = CanonicalJson.ReadStrict(
            correctedPath,
            2 * 1024 * 1024))
        {
            AuditResultSemanticValidator.Validate(Root, corrected.RootElement);
        }

        AssertInvalidTaxonomyMutation(correctedPath, root =>
            root["results"]![0]!["classification"]!["origin"] =
                "origin.unknown");
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
            "contractscribe-worker",
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
                        NetworkClaimSetRegistry.ClaimSetId,
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

    private static JsonDocument BuildHostClassificationMatrixDocument(
        JsonElement classification)
    {
        var template = classification.GetProperty("recordType").GetString()
            == "UnresolvedClassification"
            ? "unresolved-classification.json"
            : "classification-skipped.json";
        var root = JsonNode.Parse(File.ReadAllText(Path.Join(
            Root,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "payloads",
            template)))!.AsObject();
        root["results"]![0]!["classification"] =
            JsonNode.Parse(classification.GetRawText());
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static bool HostConditionsSelectRecord(JsonElement row)
    {
        var conditions = row.GetProperty("conditions").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        var classification = row.GetProperty("record");
        var origin = classification.GetProperty("origin").GetString();
        var skipReason = classification.GetProperty("skipReason").GetString();
        var knownOrigin = origin is "origin.source"
            or "origin.source-generator"
            or "origin.tool-generated"
            or "origin.mixed";
        if (conditions.Contains(
            "generated-provenance-unavailable",
            StringComparer.Ordinal))
        {
            return origin == "origin.unknown"
                && skipReason == "skip.unavailable.generated-provenance";
        }

        if (conditions.SequenceEqual(
            ["semantic-context-unavailable"],
            StringComparer.Ordinal))
        {
            return knownOrigin
                && skipReason == "skip.unavailable.semantic-context";
        }

        return conditions.SequenceEqual(
                ["documentation-comment-id-unavailable"],
                StringComparer.Ordinal)
            && knownOrigin
            && skipReason == "skip.unavailable.documentation-comment-id";
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

    private static async Task<HostCommandResult> RunHostCommandAsync(
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start Host Validation.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new HostCommandResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static void CopyHostBundleClosure(string destinationRoot)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            BundleValidator.ProtocolRelativePath,
            BundleValidator.ProtectedInputsRelativePath,
            BundleValidator.LockRelativePath,
            BundleValidator.ReviewRelativePath
        };
        using (var protocol = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            Root,
            BundleValidator.ProtocolRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)))))
        {
            paths.UnionWith(protocol.RootElement
                .GetProperty("artifactInventory")
                .EnumerateArray()
                .Select(path => path.GetString()!));
        }

        using (var protectedInputs = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(
                Root,
                BundleValidator.ProtectedInputsRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)))))
        {
            paths.UnionWith(protectedInputs.RootElement
                .GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("path").GetString()!));
        }

        foreach (var relativePath in paths)
        {
            var source = Path.Join(
                Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Join(
                destinationRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private sealed record HostCommandResult(
        int ExitCode,
        string Stdout,
        string Stderr);

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
            NetworkClaimSetRegistry.ClaimSetId,
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
