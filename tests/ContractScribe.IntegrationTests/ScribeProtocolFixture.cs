using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.IntegrationTests;

// Current protocol payload factories only; callers explicitly schedule submit and finish.
internal static class ScribeProtocolFixture
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
        using var targets = JsonDocument.Parse(request.Messages.Single(item => item.Kind == DocumentationScribeMessageKind.TargetEvidence).Content);
        return Submit(policy.RootElement, targets.RootElement, result);
    }

    internal static byte[] Submit(JsonElement policy, JsonElement targets, ReadOnlyMemory<byte> result) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "submit",
            scribeRequestSha256 = policy.GetProperty("artifactSha256").GetString(),
            sequence = targets.GetProperty("nextSequence").GetInt32(),
            items = new[] { new {
                targetId = targets.GetProperty("executableTargetIds").EnumerateArray().Single().GetString(), result = JsonNode.Parse(result.Span) } }
        });

    internal static byte[] Finish(DocumentationScribeRequest request) => JsonSerializer.SerializeToUtf8Bytes(
        new { kind = "finish", scribeRequestSha256 = request.ArtifactSha256 });

    internal static byte[] Finish(DocumentationScribeModelRequest request)
    {
        using var policy = JsonDocument.Parse(request.Messages.Single(item => item.Kind == DocumentationScribeMessageKind.RunPolicy).Content);
        return Finish(policy.RootElement);
    }

    internal static byte[] Finish(JsonElement policy) => JsonSerializer.SerializeToUtf8Bytes(
        new { kind = "finish", scribeRequestSha256 = policy.GetProperty("artifactSha256").GetString() });
}
