using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Tests;

// Current singleton fixture editing only. This never executes a model or supplies a finish response.
internal static class ScribeResultFixture
{
    internal static byte[] Submit(DocumentationScribeRequest request, ReadOnlyMemory<byte> result, int sequence = 1) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "submit",
            scribeRequestSha256 = request.ArtifactSha256,
            sequence,
            items = new[] { new { targetId = request.ExecutableTargetIds.Single(), result = JsonNode.Parse(result.Span) } }
        });

    internal static byte[] Submit(DocumentationScribeModelRequest request, ReadOnlyMemory<byte> result)
    {
        using var policy = JsonDocument.Parse(request.Messages.Single(item => item.Kind == DocumentationScribeMessageKind.RunPolicy).Content);
        using var target = JsonDocument.Parse(request.Messages.Single(item => item.Kind == DocumentationScribeMessageKind.TargetEvidence).Content);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "submit",
            scribeRequestSha256 = policy.RootElement.GetProperty("artifactSha256").GetString(),
            sequence = target.RootElement.GetProperty("nextSequence").GetInt32(),
            items = new[] { new {
                targetId = target.RootElement.GetProperty("executableTargetIds").EnumerateArray().Single().GetString(), result = JsonNode.Parse(result.Span) } }
        });
    }

    internal static byte[] Finish(DocumentationScribeRequest request) => JsonSerializer.SerializeToUtf8Bytes(
        new { kind = "finish", scribeRequestSha256 = request.ArtifactSha256 });

    internal static byte[] Finish(DocumentationScribeModelRequest request)
    {
        using var policy = JsonDocument.Parse(request.Messages.Single(item => item.Kind == DocumentationScribeMessageKind.RunPolicy).Content);
        return JsonSerializer.SerializeToUtf8Bytes(new { kind = "finish", scribeRequestSha256 = policy.RootElement.GetProperty("artifactSha256").GetString() });
    }

    internal static JsonNode Payload(JsonNode result) => result["submissions"] is JsonArray { Count: 1 } submissions
        ? submissions[0]!["result"]! : result["stop"]!;

    internal static void RefreshCoverage(JsonObject result, DocumentationScribeRequest request)
    {
        var submissions = result["submissions"]!.AsArray();
        var failures = result["targetFailures"]!.AsArray();
        result["coverage"] = new JsonArray(request.Manifest.Select(member =>
        {
            var submission = submissions.SingleOrDefault(item => item!["targetId"]!.GetValue<string>() == member.TargetId);
            var status = submission is not null ? submission["result"]!["kind"]!.GetValue<string>() == "skip" ? "skipped"
                : submission["stage"]!.GetValue<string>() : failures.Any(item => item!["targetId"]!.GetValue<string>() == member.TargetId)
                    ? "failed" : member.AuditOutcome == AuditOutcome.Compliant ? "compliant" : "remaining";
            var row = new JsonObject { ["targetId"] = member.TargetId, ["auditOutcome"] = AuditVocabulary.GetId(member.AuditOutcome), ["status"] = status };
            if (submission is not null) row["stage"] = submission["stage"]!.DeepClone();
            return (JsonNode)row;
        }).ToArray());
        if (result["stop"]!["kind"]!.GetValue<string>() == "finished")
            result["stop"]!["reason"] = result["coverage"]!.AsArray().All(row => row!["status"]!.GetValue<string>()
                is "compliant" or "validated" or "durably-recorded" or "patch-accepted") ? "completed" : "unresolved";
    }

    internal static byte[] Serialize(JsonObject value)
    {
        RefreshCommitments(value);
        return Encoding.UTF8.GetBytes(value.ToJsonString());
    }

    internal static void RefreshCommitments(JsonObject value)
    {
        if (value["submissions"] is not JsonArray submissions) return;
        foreach (var item in submissions.OfType<JsonObject>())
        {
            if (item["result"] is not { } result) continue;
            var bytes = CanonicalJson.Normalize(Encoding.UTF8.GetBytes(result.ToJsonString()));
            item["contentSha256"] = Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).ToLowerInvariant();
        }
    }
}
