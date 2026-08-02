using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;

namespace ContractScribe.Tests;

public sealed class M1HostValidationMeasuredBoundSchemaTests
{
    private const string Sha =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string HostRevision =
        "2222222222222222222222222222222222222222";
    private static readonly string Root = FindRepositoryRoot();
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
    private static readonly Lazy<string> StandaloneResponseSchema = new(() =>
        MaterializeDefinitionSchema(SubjectSchema, "subjectResponse"));
    private static readonly Lazy<string> CopiedResponseSchema = new(() =>
        MaterializeDefinitionSchema(CellEvidenceSchema, "subject"));

    [Fact]
    public void MeasuredBounds_ResponseSchemasAdmitExactlyTheNormativeTuples()
    {
        var valid = new HashSet<(string Name, string Unit, string EnforcementClass)>
        {
            ("diagnostic-count", "count", "internally-enforceable"),
            ("diagnostic-utf8-bytes", "bytes", "internally-enforceable"),
            ("temporary-disk-bytes", "bytes", "internally-enforceable"),
            ("toolchain-subprocess-count", "count", "observable-only")
        };

        foreach (var name in new[]
                 {
                     "diagnostic-count",
                     "diagnostic-utf8-bytes",
                     "temporary-disk-bytes",
                     "toolchain-subprocess-count"
                 })
        {
            foreach (var unit in new[] { "bytes", "count" })
            {
                foreach (var enforcementClass in new[]
                         {
                             "internally-enforceable",
                             "observable-only"
                         })
                {
                    var tuple = (name, unit, enforcementClass);
                    var bound = new MeasuredBoundFact(
                        name,
                        unit,
                        1,
                        16,
                        enforcementClass);
                    AssertSchemaParity(
                        ResponseNode(SchemaOnlySubject(bound)),
                        valid.Contains(tuple));
                }
            }
        }
    }

    [Fact]
    public void MeasuredBounds_SameResponsesPassBothSchemasAndVectorSemantics()
    {
        var temp = Path.Join(
            Root,
            "TestResults",
            $"host-validation-measured-bound-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var boundsPath = Path.Join(temp, "bounds.json");
            WriteCalibratedBounds(boundsPath);
            SchemaValidation.Validate(
                boundsPath,
                Path.Join(
                    Root,
                    "schemas",
                    "validation",
                    "m1-host-calibrated-bounds-v1.schema.json"),
                requireCanonical: true);
            var identity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, boundsPath),
                CanonicalJson.Sha256File(boundsPath));
            var source = Source(identity);
            var materialization = ExactMaterialization();
            var context = BundleValidator.Validate(Root);
            var diagnosticBytes = RunSemantics.MeasureCanonicalDiagnosticBytes([]);

            var cases = new[]
            {
                new SemanticCase(
                    "diagnostics.bounded-sanitized",
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
                            diagnosticBytes,
                            65536,
                            "internally-enforceable")
                    ],
                    BasicProcess(),
                    [new ObservedProcess(700, 1, "subject-runtime", "dotnet")]),
                new SemanticCase(
                    "bounds.temporary-disk",
                    [
                        new MeasuredBoundFact(
                            "temporary-disk-bytes",
                            "bytes",
                            2048,
                            1048576,
                            "internally-enforceable")
                    ],
                    new ProcessObservation(
                        0,
                        "started",
                        "normal",
                        false,
                        true,
                        true,
                        "temporary-disk-high-water",
                        "measure-temporary-disk",
                        false,
                        TemporaryDiskHighWater: new TemporaryDiskHighWaterEvidence(
                            "peak-concurrent-logical-file-bytes",
                            "contractscribe-temporary-work-and-output-staging.v1",
                            "pre-subject-to-temporary-disk-high-water.v1",
                            1024,
                            1024,
                            2048,
                            true,
                            false)),
                    [new ObservedProcess(700, 1, "subject-runtime", "dotnet")]),
                new SemanticCase(
                    "toolchain.owned-subprocesses",
                    [
                        new MeasuredBoundFact(
                            "toolchain-subprocess-count",
                            "count",
                            1,
                            16,
                            "observable-only")
                    ],
                    new ProcessObservation(
                        0,
                        "started",
                        "normal",
                        false,
                        true,
                        true,
                        "process-observation",
                        "observe",
                        true),
                    [
                        new ObservedProcess(700, 1, "subject-runtime", "dotnet"),
                        new ObservedProcess(701, 700, "toolchain-owned", "dotnet")
                    ])
            };

            foreach (var item in cases)
            {
                var vector = context.Vectors.Vectors.Single(candidate =>
                    candidate.VectorId == item.VectorId);
                var subject = SuccessfulSubject(
                    vector,
                    source,
                    materialization,
                    item.Bounds);
                AssertSchemaParity(ResponseNode(subject), expected: true);

                var derived = RunSemantics.Derive(
                    context,
                    vector,
                    Run(vector, subject, item.Process, item.ObservedProcesses),
                    Fixture(vector),
                    source,
                    materialization);
                Assert.Equal("matched", derived.Verdict);
                Assert.Equal(vector.ExpectedObservation, derived.Observation);
                Assert.Equal(vector.ExpectedEnforcementClass, derived.EnforcementClass);
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static SubjectResponse SchemaOnlySubject(MeasuredBoundFact bound)
    {
        var commitment = Commitment();
        return new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            "self-test.fake-subject",
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
            bound.EnforcementClass,
            "self-test.measured-bounds",
            commitment,
            new HostObservationFacts(
                $"source.{Sha}",
                HostRevision,
                Sha,
                Sha,
                Sha,
                "10.0.102",
                "10.0.0",
                "18.0.0",
                [],
                new OutputCommitFact("committed", Sha),
                [bound]));
    }

    private static SubjectResponse SuccessfulSubject(
        VectorDefinition vector,
        SubjectSourceConfiguration source,
        CellMaterialization materialization,
        IReadOnlyList<MeasuredBoundFact> bounds)
    {
        var commitment = Commitment();
        return new SubjectResponse(
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
            commitment,
            new HostObservationFacts(
                source.SourceConfigurationId,
                source.HostRevision,
                source.ContractBaseline.Sha256,
                source.FailureRegistry.Sha256,
                source.CalibratedBounds.Sha256,
                materialization.SelectedSdk,
                materialization.SelectedRuntime,
                materialization.SelectedMsbuild,
                [],
                new OutputCommitFact("committed", commitment.Sha256),
                bounds));
    }

    private static RunEvidence Run(
        VectorDefinition vector,
        SubjectResponse subject,
        ProcessObservation process,
        IReadOnlyList<ObservedProcess> observedProcesses) => new(
        vector.VectorId,
        "run-1",
        "unvalidated",
        vector.ExpectedObservation,
        "unvalidated",
        vector.ExpectedEnforcementClass,
        "unvalidated",
        subject,
        process,
        subject.CanonicalResult,
        null,
        EmptyDelta(),
        observedProcesses,
        []);

    private static FixtureRealization Fixture(VectorDefinition vector) => new(
        vector.VectorId,
        vector.ExecutorKind,
        "tests/fixtures",
        Sha,
        true,
        null,
        null,
        [],
        null,
        [],
        [],
        vector.VectorId == "toolchain.owned-subprocesses"
            ? "synchronized-tree"
            : "bounded-polling",
        "TestResults/audit-result.json",
        "absent",
        [new RunWorkingDirectory("run-1", "repository-root")],
        null);

    private static ProcessObservation BasicProcess() => new(
        0,
        "started",
        "normal",
        false,
        false,
        true);

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

    private static SubjectSourceConfiguration Source(ArtifactIdentity identity) => new(
        $"source.{Sha}",
        HostRevision,
        $"operations.{Sha}",
        [],
        [],
        identity,
        identity,
        identity,
        identity,
        identity,
        identity,
        identity);

    private static CellMaterialization ExactMaterialization() => new(
        "ubuntu-x64",
        "1",
        "https://example.invalid/jobs/1",
        "self-test",
        "linux-x64",
        "X64",
        "10.0.102",
        "10.0.0",
        "18.0.0",
        []);

    private static CanonicalResultCommitment Commitment() => new(
        Sha,
        1,
        "canonical-json-utf8-no-bom-single-lf",
        true);

    private static void WriteCalibratedBounds(string path) =>
        CanonicalJson.WriteCanonical(
            path,
            new
            {
                boundsVersion = 1,
                entries = new[]
                {
                    BoundEntry("sdk-discovery-timeout", "milliseconds", 30000, "internally-enforceable"),
                    BoundEntry("workspace-load-timeout", "milliseconds", 30000, "internally-enforceable"),
                    BoundEntry("total-audit-timeout", "milliseconds", 120000, "internally-enforceable"),
                    BoundEntry("graceful-shutdown-timeout", "milliseconds", 30000, "internally-enforceable"),
                    BoundEntry("diagnostic-count", "count", 100, "internally-enforceable"),
                    BoundEntry("diagnostic-utf8-bytes", "bytes", 65536, "internally-enforceable"),
                    BoundEntry("temporary-disk-bytes", "bytes", 1048576, "internally-enforceable"),
                    BoundEntry("toolchain-subprocess-count", "count", 16, "observable-only")
                },
                formatVersion = "contractscribe-host-calibrated-bounds-v1"
            });

    private static object BoundEntry(
        string name,
        string unit,
        long limit,
        string enforcementClass) => new
        {
            calibrationEvidenceSha256 = Sha,
            enforcementClass,
            limit,
            name,
            unit
        };

    private static JsonObject ResponseNode(SubjectResponse subject) =>
        JsonNode.Parse(Encoding.UTF8.GetString(
            CanonicalJson.SerializeCanonical(subject)))!.AsObject();

    private static void AssertSchemaParity(JsonObject response, bool expected)
    {
        var standalone = SchemaAccepts(response, StandaloneResponseSchema.Value);
        var copied = SchemaAccepts(response, CopiedResponseSchema.Value);
        Assert.Equal(standalone, copied);
        Assert.Equal(expected, standalone);
    }

    private static bool SchemaAccepts(JsonObject response, string schemaPath)
    {
        var path = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-measured-bound-{Guid.NewGuid():N}.json");
        try
        {
            using (var document = JsonDocument.Parse(response.ToJsonString()))
            {
                File.WriteAllBytes(
                    path,
                    CanonicalJson.SerializeCanonical(document.RootElement));
            }
            SchemaValidation.Validate(path, schemaPath, requireCanonical: true);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MaterializeDefinitionSchema(
        string schemaPath,
        string definition)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        var schema = root["$defs"]![definition]!.DeepClone().AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$defs"] = root["$defs"]!.DeepClone();
        var path = Path.Join(
            Path.GetTempPath(),
            $"contractscribe-measured-bound-schema-{Guid.NewGuid():N}.json");
        using (var materialized = JsonDocument.Parse(schema.ToJsonString()))
        {
            File.WriteAllBytes(
                path,
                CanonicalJson.SerializeCanonical(materialized.RootElement));
        }
        return path;
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
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record SemanticCase(
        string VectorId,
        IReadOnlyList<MeasuredBoundFact> Bounds,
        ProcessObservation Process,
        IReadOnlyList<ObservedProcess> ObservedProcesses);
}
