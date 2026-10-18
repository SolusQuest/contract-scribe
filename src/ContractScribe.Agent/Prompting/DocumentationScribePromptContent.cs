using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Agent.Prompting;

// Rebuilt from the visible seed/history on every request, including after a retry.
// This projects bytes only; evidence subjects, commitments and continuation stay intact.
internal sealed class DocumentationScribePromptContent(DocumentationScribeRequest request)
{
    private readonly HashSet<string> visible = new(StringComparer.Ordinal);

    internal object Evidence(DocumentationScribeEvidenceContent content)
    {
        var reference = request.EvidenceReferences.Single(item => item.EvidenceReferenceId == content.EvidenceReferenceId);
        var node = JsonNode.Parse(CanonicalJson.Serialize(content).AsSpan())!.AsObject();
        if (reference.Locator is RepositoryEvidenceLocator locator)
            Share(node, locator.Path, reference.ContentSha256,
                !reference.IsTruncated ? "complete"
                    : locator.Span is { } span ? $"{span.Start}:{span.End}" : "partial", content.Content);
        return node;
    }

    internal ImmutableArray<DocumentationScribeCompletedToolExchange> History(
        ImmutableArray<DocumentationScribeCompletedToolExchange> exchanges) => exchanges.Select(exchange =>
        {
            if (!exchange.OperationId.StartsWith("repository.", StringComparison.Ordinal)
                && exchange.OperationId != "get-target-evidence")
                return exchange;
            var node = JsonNode.Parse(exchange.ResultUtf8Json.Span)!;
            Project(node);
            return new DocumentationScribeCompletedToolExchange(exchange.ResponseIndex, exchange.CallId,
                exchange.OperationId, exchange.ArgumentsUtf8JsonStorage, exchange.OutcomeId,
                CanonicalJson.Normalize(Encoding.UTF8.GetBytes(node.ToJsonString())), exchange.EvidenceReferences,
                exchange.AssistantContinuation);
        }).ToImmutableArray();

    private void Project(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
                Project(child);
            return;
        }
        if (node is not JsonObject value) return;
        if (value["content"] is JsonValue text && text.TryGetValue<string>(out var content))
        {
            if (value["repositoryPath"] is JsonValue path && value["contentSha256"] is JsonValue sha
                && value["startUtf16"] is JsonValue start && value["endUtf16"] is JsonValue end)
                Share(value, path.GetValue<string>(), sha.GetValue<string>(),
                    value["isTruncated"]?.GetValue<bool>() == false ? "complete"
                        : start.GetValue<int>() + ":" + end.GetValue<int>(), content);
            else if (value["commitment"] is JsonObject commitment
                && commitment["repositoryPath"] is JsonValue sourcePath
                && commitment["contentSha256"] is JsonValue sourceSha)
                Share(value, sourcePath.GetValue<string>(), sourceSha.GetValue<string>(),
                    commitment["isTruncated"]?.GetValue<bool>() == false ? "complete"
                        : value["includedRange"] is JsonObject range ? range["start"]!.GetValue<int>() + ":" + range["end"]!.GetValue<int>()
                        : "partial", content);
        }
        // Copy children because Share may replace content with a reference.
        foreach (var child in value.Select(property => property.Value)
            .Where(child => child is JsonObject or JsonArray).ToArray())
            Project(child!);
    }

    private void Share(JsonObject value, string path, string sourceSha, string range, string content)
    {
        var identity = CanonicalJson.Serialize(new
        {
            path,
            sourceSha,
            range,
            contentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        });
        var id = "content." + Convert.ToHexString(SHA256.HashData(identity.AsSpan())).ToLowerInvariant();
        if (visible.Add(id)) value["contentId"] = id;
        else
        {
            value.Remove("content");
            value["contentRef"] = id;
        }
    }
}
