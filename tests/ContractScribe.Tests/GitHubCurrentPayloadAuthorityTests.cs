using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("actor-association")]
    [InlineData("contents-permission")]
    [InlineData("current-payload")]
    public async Task Warm_raw_objects_never_replace_the_current_contents_permission_actor_or_physical_head(string fault)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var stopped = CampaignStateReducer.Stop(PublicationCheckpointFixture("campaign-1"), CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, (await CampaignCheckpointAcceptance.AcceptAsync(store, stopped)).Kind);
        Assert.Equal(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
        // The positive pool now contains actual independently verified bytes at
        // this signed head. Its principal association is not part of Git bytes.
        var head = remote.CoordinationHead;
        var effects = remote.RefMutationAttempts;
        var intercepted = false;
        handler.AfterResponse = async (request, response) =>
        {
            if (fault == "contents-permission" && request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/contract-scribe/coordination/", StringComparison.Ordinal))
            {
                intercepted = true;
                response.Dispose();
                return new(HttpStatusCode.Forbidden);
            }
            if (fault != "contents-permission" && IsCheckpointQuery(request, currentOnly: true))
            {
                intercepted = true;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                var repository = json["data"]!["repository"]!;
                if (fault == "actor-association")
                {
                    var actor = repository["coord"]!["target"]!["author"]!["user"]!;
                    actor["id"] = "FOREIGN_PRINCIPAL";
                    actor["databaseId"] = 999;
                    actor["login"] = "different-user";
                }
                else
                {
                    var blob = repository["checkpoint"]!;
                    var bytes = Encoding.UTF8.GetBytes(blob["text"]!.GetValue<string>() + " ");
                    blob["text"] = Encoding.UTF8.GetString(bytes);
                    blob["byteSize"] = bytes.Length; // Same OID, different bytes must fail.
                }
                response.Content.Dispose();
                response.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return response;
        };
        try
        {
            Assert.NotEqual(CampaignCheckpointReadKind.Found, (await store.ReadAsync(default)).Kind);
            Assert.True(intercepted);
            Assert.Equal(head, remote.CoordinationHead);
            Assert.Equal(effects, remote.RefMutationAttempts);
        }
        finally { handler.AfterResponse = null; }
    }
}
