using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.HostValidation;

namespace ContractScribe.Tests;

public sealed class M1ContractBaselineHostConsumerTests
{
    private static readonly string Root = FindRepositoryRoot();

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
        var rows = matrix.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("caseId").GetString()!,
                row => row,
                StringComparer.Ordinal);
        foreach (var (caseId, row) in rows)
        {
            var recordAccepted = HostAcceptsClassificationRecord(
                row.GetProperty("record"));
            var selectionAccepted = HostConditionsSelectRecord(row);
            Assert.True(
                recordAccepted
                    == (row.GetProperty("recordOutcome").GetString() == "accept"),
                caseId);
            Assert.True(
                selectionAccepted
                    == (row.GetProperty("selectionOutcome").GetString() == "accept"),
                caseId);
            Assert.True(
                (recordAccepted && selectionAccepted)
                    == (row.GetProperty("outcome").GetString() == "accept"),
                caseId);
        }

        foreach (var caseId in RepresentativeClassificationRejections)
        {
            Assert.False(
                HostAcceptsClassificationRecord(
                    rows[caseId].GetProperty("record")),
                caseId);
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
        var selectedSkipReason = row.GetProperty("record")
            .GetProperty("skipReason")
            .GetString();
        var expectedSkipReason = conditions.Contains(
            "documentation-comment-id-unavailable",
            StringComparer.Ordinal)
            ? "skip.unavailable.documentation-comment-id"
            : conditions.Contains(
                "generated-provenance-unavailable",
                StringComparer.Ordinal)
                ? "skip.unavailable.generated-provenance"
                : conditions.Contains(
                    "semantic-context-unavailable",
                    StringComparer.Ordinal)
                    ? "skip.unavailable.semantic-context"
                    : null;
        return selectedSkipReason == expectedSkipReason;
    }

    private static bool HostAcceptsClassificationRecord(JsonElement classification)
    {
        using var document = BuildHostClassificationMatrixDocument(
            classification);
        try
        {
            AuditResultSemanticValidator.Validate(
                Root,
                document.RootElement);
            return true;
        }
        catch (ProtocolException exception)
            when (exception.Code == "HV230_AUDIT_RESULT_SEMANTICS")
        {
            return false;
        }
    }

    private static readonly string[] RepresentativeClassificationRejections =
    [
        "target.generated-provenance.source-origin.reject",
        "target.semantic-context.unknown-origin.reject",
        "target.generated-provenance.compiler-synthesized-origin.reject",
        "unresolved.documentation-comment-id.unknown-origin.reject",
        "component.semantic-context.component.accessor.get.ineligible.reject"
    ];

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
}
