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
                    AssertSchemaParity(
                        ResponseNode(new MeasuredBoundFact(
                            name,
                            unit,
                            1,
                            16,
                            enforcementClass)),
                        valid.Contains(tuple));
                }
            }
        }
    }

    [Fact]
    public void MeasuredBounds_DiagnosticAndToolchainTuplesReachSharedSemantics()
    {
        var temp = Path.Join(
            Root,
            "TestResults",
            $"host-validation-measured-bound-schema-{Guid.NewGuid():N}");
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
                        },
                        new
                        {
                            name = "toolchain-subprocess-count",
                            unit = "count",
                            limit = 16L,
                            enforcementClass = "observable-only"
                        }
                    },
                    formatVersion = "contractscribe-m1-host-calibrated-bounds-v1"
                });
            var identity = new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(Root, boundsPath),
                CanonicalJson.Sha256File(boundsPath));
            var source = new SubjectSourceConfiguration(
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

            var diagnostics = new[]
            {
                new NormalizedDiagnosticFact("host.synthetic", "audit")
            };
            var diagnosticBytes = RunSemantics.MeasureCanonicalDiagnosticBytes(diagnostics);
            var diagnosticFacts = Facts(
                source,
                diagnostics,
                [
                    new MeasuredBoundFact(
                        "diagnostic-count",
                        "count",
                        diagnostics.Length,
                        100,
                        "internally-enforceable"),
                    new MeasuredBoundFact(
                        "diagnostic-utf8-bytes",
                        "bytes",
                        diagnosticBytes,
                        65536,
                        "internally-enforceable")
                ]);
            RunSemantics.ValidateMeasuredBounds(
                Root,
                source,
                diagnosticFacts,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["diagnostic-count"] = diagnostics.Length,
                    ["diagnostic-utf8-bytes"] = diagnosticBytes
                });

            var toolchainFacts = Facts(
                source,
                [],
                [
                    new MeasuredBoundFact(
                        "toolchain-subprocess-count",
                        "count",
                        1,
                        16,
                        "observable-only")
                ]);
            RunSemantics.ValidateMeasuredBounds(
                Root,
                source,
                toolchainFacts,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["toolchain-subprocess-count"] = 1
                });
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static HostObservationFacts Facts(
        SubjectSourceConfiguration source,
        IReadOnlyList<NormalizedDiagnosticFact> diagnostics,
        IReadOnlyList<MeasuredBoundFact> bounds) => new(
        source.SourceConfigurationId,
        source.HostRevision,
        source.ContractBaseline.Sha256,
        source.FailureRegistry.Sha256,
        source.CalibratedBounds.Sha256,
        "10.0.102",
        "10.0.0",
        "18.0.0",
        diagnostics,
        new OutputCommitFact("not-committed", null),
        bounds);

    private static JsonObject ResponseNode(MeasuredBoundFact bound)
    {
        var commitment = new CanonicalResultCommitment(
            Sha,
            1,
            "canonical-json-utf8-no-bom-single-lf",
            true);
        var subject = new SubjectResponse(
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
            "internally-enforceable",
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
        return JsonNode.Parse(Encoding.UTF8.GetString(
            CanonicalJson.SerializeCanonical(subject)))!.AsObject();
    }

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
}
