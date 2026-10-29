using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed partial class GitHubProposalLoopbackServer
{
    // Independently fixed wire contract, not a permissive generic GraphQL handler.
    private const string CampaignSnapshotQuery = """
        query CampaignPlatform($owner:String!,$name:String!,$target:String!,$coord:String!,$after:String){
          repository(owner:$owner,name:$name,followRenames:false){
            ...CampaignRepository isPrivate isArchived isDisabled viewerPermission
            base:ref(qualifiedName:$target){id prefix name target{__typename oid}}
            coord:ref(qualifiedName:$coord){id prefix name target{__typename oid}}
            pullRequests(first:100,after:$after,orderBy:{field:CREATED_AT,direction:ASC}){
              totalCount pageInfo{hasNextPage endCursor}
              nodes{id fullDatabaseId number state isDraft merged mergedAt closedAt createdAt title body maintainerCanModify
                headRefName headRefOid baseRefName baseRefOid
                headRepository{...CampaignRepository} baseRepository{...CampaignRepository}
                author{...CampaignActor}
              }
            }
          }
        }
        fragment CampaignRepository on Repository{
          id fullDatabaseId name owner{...CampaignActor}
        }
        fragment CampaignActor on Actor{
          __typename login ... on Node{id}
          ... on User{databaseId} ... on Bot{databaseId}
          ... on Organization{databaseId} ... on Mannequin{databaseId}
        }
        """;

    private JsonObject CampaignSnapshot(JsonObject variables)
    {
        Assert.Equal(new[] { "after", "coord", "name", "owner", "target" }, variables.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Owner", variables["owner"]!.GetValue<string>());
        Assert.Equal("repo", variables["name"]!.GetValue<string>());
        Assert.Equal("refs/heads/main", variables["target"]!.GetValue<string>());
        var cursor = variables["after"]?.GetValue<string>();
        var offset = 0;
        if (cursor is not null)
        {
            Assert.StartsWith("offset:", cursor);
            Assert.True(int.TryParse(cursor[7..], NumberStyles.None, CultureInfo.InvariantCulture, out offset));
            Assert.InRange(offset, 1, PullRequests.Count);
        }
        foreach (var pr in PullRequests)
        {
            if (Refs.TryGetValue("refs/heads/" + pr["head"]!["ref"]!.GetValue<string>(), out var head))
                pr["head"]!["sha"] = head;
            if (pr["state"]!.GetValue<string>() == "open") pr["base"]!["sha"] = Refs["refs/heads/main"];
        }
        var rows = PullRequests.OrderBy(pr => pr["created_at"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(pr => pr["number"]!.GetValue<int>()).Skip(offset).Take(100).Select(Project).ToArray();
        var next = offset + rows.Length;
        var repo = ProjectRepository(JsonSerializer.SerializeToNode(Repository())!)!;
        repo["isPrivate"] = false; repo["isArchived"] = false; repo["isDisabled"] = false; repo["viewerPermission"] = null;
        repo["base"] = Reference(variables["target"]!.GetValue<string>());
        repo["coord"] = Reference(variables["coord"]!.GetValue<string>());
        repo["pullRequests"] = new JsonObject
        {
            ["totalCount"] = PullRequests.Count,
            ["nodes"] = new JsonArray(rows),
            ["pageInfo"] = new JsonObject
            {
                ["hasNextPage"] = next < PullRequests.Count,
                ["endCursor"] = rows.Length == 0 ? null : "offset:" + next.ToString(CultureInfo.InvariantCulture)
            }
        };
        return new() { ["data"] = new JsonObject { ["repository"] = repo } };

        JsonObject? Reference(string reference) => Refs.TryGetValue(reference, out var oid)
            ? new()
            {
                ["id"] = "REF_node",
                ["prefix"] = "refs/heads/",
                ["name"] = reference[11..],
                ["target"] = new JsonObject { ["__typename"] = "Commit", ["oid"] = oid }
            } : null;
    }

    private static JsonObject? ProjectRepository(JsonNode? value) => value is null ? null : new()
    {
        ["id"] = value["node_id"]!.DeepClone(),
        ["fullDatabaseId"] = value["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture),
        ["name"] = value["name"]!.DeepClone(),
        ["owner"] = ProjectActor(value["owner"])
    };

    private static JsonObject? ProjectActor(JsonNode? value) => value is null ? null : new()
    {
        ["__typename"] = value["type"]!.DeepClone(),
        ["login"] = value["login"]!.DeepClone(),
        ["id"] = value["node_id"]!.DeepClone(),
        ["databaseId"] = value["id"]!.DeepClone()
    };

    private static JsonObject Project(JsonObject value) => new()
    {
        ["id"] = value["node_id"]!.DeepClone(),
        ["fullDatabaseId"] = value["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture),
        ["number"] = value["number"]!.DeepClone(),
        ["state"] = value["merged"]?.GetValue<bool>() == true ? "MERGED"
            : value["state"]!.GetValue<string>() == "open" ? "OPEN" : "CLOSED",
        ["isDraft"] = value["draft"]!.DeepClone(),
        ["merged"] = value["merged"]!.DeepClone(),
        ["mergedAt"] = value["merged_at"]?.DeepClone(),
        ["closedAt"] = value["closed_at"]?.DeepClone(),
        ["createdAt"] = value["created_at"]!.DeepClone(),
        ["title"] = value["title"]!.DeepClone(),
        ["body"] = value["body"]?.DeepClone() ?? JsonValue.Create(""),
        ["maintainerCanModify"] = value["maintainer_can_modify"]!.DeepClone(),
        ["headRepository"] = ProjectRepository(value["head"]!["repo"]),
        ["baseRepository"] = ProjectRepository(value["base"]!["repo"]),
        ["headRefName"] = value["head"]!["ref"]!.DeepClone(),
        ["headRefOid"] = value["head"]!["sha"]!.DeepClone(),
        ["baseRefName"] = value["base"]!["ref"]!.DeepClone(),
        ["baseRefOid"] = value["base"]!["sha"]!.DeepClone(),
        ["author"] = ProjectActor(value["user"])
    };
}
