using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

        var context = BundleValidator.Validate(Root);
        var vector = context.Vectors.Vectors.First(candidate =>
            candidate.ExecutorKind == "production-host"
            && candidate.VectorId != "repository-write.protected-files");
        var sha = new string('1', 64);
        var artifact = new ArtifactIdentity("src/ContractScribe.Core/ContractScribe.Core.csproj", sha);
        var source = new SubjectSourceConfiguration(
            $"source.{sha}",
            new string('1', 40),
            ["src/ContractScribe.Core"],
            [artifact],
            artifact,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact,
            artifact);
        var fixture = new FixtureRealization(
            vector.VectorId,
            vector.ExecutorKind,
            "tests/fixtures",
            sha,
            true,
            null,
            null,
            [],
            null,
            [],
            [],
            "bounded-polling",
            null,
            null);
        var subject = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vector.VectorId,
            "run-1",
            "started",
            "normal",
            "compliant",
            "succeeded",
            null,
            null,
            null,
            "committed",
            "published",
            vector.ExpectedEnforcementClass,
            vector.ExpectedObservation,
            null);
        var run = new RunEvidence(
            vector.VectorId,
            "run-1",
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            subject,
            new ProcessObservation(0, "started", "normal", false, true, true),
            null,
            new RepositoryDelta([], [], ["Sample.cs"], [], [], [], [], [], []),
            [new ObservedProcess(100, 1, "subject-runtime", "dotnet")],
            []);
        var derived = RunSemantics.Derive(context, vector, run, fixture, source);
        Assert.Equal("repository.protected-mutation-unexpected", derived.Observation);
        Assert.Equal("subject-nonconformance", derived.Verdict);

        var contradiction = run with
        {
            Process = run.Process with { ProcessTermination = "crash" }
        };
        var failure = Assert.Throws<ProtocolException>(() =>
            RunSemantics.Derive(context, vector, contradiction, fixture, source));
        Assert.Equal("HV209_SUBJECT_OBSERVER_CONTRADICTION", failure.Code);
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

    private static (ExecutionSubjectManifest Subject, CellEvidence Evidence) CreateSyntheticIncompleteCell(
        BundleContext context,
        string cellId)
    {
        var sha = new string('1', 64);
        var commit = new string('1', 40);
        var artifact = new ArtifactIdentity("src/ContractScribe.Core/ContractScribe.Core.csproj", sha);
        var source = new SubjectSourceConfiguration(
            $"source.{sha}",
            commit,
            ["src/ContractScribe.Core"],
            [artifact],
            artifact,
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
