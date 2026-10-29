using System.Text.Json;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

internal sealed partial class GitHubApiClient
{
    internal const string CommitCheckpointDocument = "mutation($input:CreateCommitOnBranchInput!){createCommitOnBranch(input:$input){clientMutationId}}";

    // Mutations are single use, never replayed, and deliberately return no OID.
    // An acknowledgement cannot authenticate either the commit or the ref.
    internal ValueTask<GitHubApiResult<GitHubAcknowledgement>> CommitCheckpointAsync(
        GitHubAtomicCheckpointCommit operation, CancellationToken token) => RunAsync<GitHubAcknowledgement>(() =>
    {
        Input(checkpointAuthority is not null && checkpointWrite is not null
            && ReferenceEquals(operation.Write, checkpointWrite) && operation.Context.Repository == Identity());
        Input(operation.TryConsume());
        var context = operation.Context;
        var body = Encode(writer =>
        {
            writer.WriteString("query", CommitCheckpointDocument);
            writer.WriteStartObject("variables");
            writer.WriteStartObject("input");
            writer.WriteStartObject("branch");
            // Immutable Ref identity prevents a transferred/recreated namespace
            // from redirecting this conditional write to a different repo.
            writer.WriteString("id", context.RefNodeId);
            writer.WriteEndObject();
            writer.WriteString("expectedHeadOid", context.BeforeOid);
            writer.WriteString("clientMutationId", context.ClientMutationId);
            writer.WriteStartObject("message");
            var separator = context.Message.IndexOf("\n\n", StringComparison.Ordinal);
            writer.WriteString("headline", context.Message[..separator]);
            writer.WriteString("body", context.Message[(separator + 2)..]);
            writer.WriteEndObject();
            writer.WriteStartObject("fileChanges");
            writer.WriteStartArray("additions");
            foreach (var (path, bytes) in operation.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteBase64String("contents", bytes.AsSpan());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        return new("graphql", element => AtomicAcknowledgement(element, context.ClientMutationId), body, context, Status: 200);
    }, token);

    private static GitHubAcknowledgement AtomicAcknowledgement(JsonElement value, string expected)
    {
        // The ordinary closed error parser already rejects partial/unknown
        // shapes. Keep this candidate equally closed; an error stays ambiguous.
        Exact(value, "data");
        var data = Property(value, "data");
        Exact(data, "createCommitOnBranch");
        var payload = Property(data, "createCommitOnBranch");
        Exact(payload, "clientMutationId");
        Require(String(payload, "clientMutationId", 128) == expected);
        return new();
    }
}
