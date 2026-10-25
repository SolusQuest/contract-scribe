using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Theory]
    [InlineData("missing-blob")]
    [InlineData("missing-tree")]
    [InlineData("blob-as-tree")]
    [InlineData("tree-as-blob")]
    [InlineData("type-mode")]
    [InlineData("duplicate-path")]
    [InlineData("missing-commit-tree")]
    [InlineData("blob-as-commit-tree")]
    [InlineData("missing-parent")]
    [InlineData("tree-as-parent")]
    public async Task Git_object_fixture_rejects_invalid_graph_before_creating_objects(string fault)
    {
        await using var server = new GitHubProposalLoopbackServer(new Dictionary<string, byte[]> { ["seed.txt"] = "seed"u8.ToArray() });
        using var client = ObjectClient(server);
        var blob = server.Blobs.Keys.Single();
        var tree = server.Commits[server.BaseOid]["tree"]!["sha"]!.GetValue<string>();
        var missing = new string('0', 40);
        var entry = new JsonObject { ["path"] = "file.txt", ["mode"] = "100644", ["type"] = "blob", ["sha"] = blob };
        if (fault is "missing-tree" or "blob-as-tree") { entry["mode"] = "040000"; entry["type"] = "tree"; }
        if (fault is "missing-tree" or "missing-blob") entry["sha"] = missing;
        if (fault == "tree-as-blob") entry["sha"] = tree;
        if (fault == "type-mode") entry["type"] = "tree";
        var entries = new JsonArray(entry);
        if (fault == "duplicate-path") entries.Add(entry.DeepClone());
        var commitFault = fault.Contains("commit", StringComparison.Ordinal) || fault.Contains("parent", StringComparison.Ordinal);
        JsonObject body = commitFault ? CommitBody(tree, server.BaseOid) : new() { ["tree"] = entries };
        if (fault == "missing-commit-tree") body["tree"] = missing;
        if (fault == "blob-as-commit-tree") body["tree"] = blob;
        if (fault == "missing-parent") body["parents"] = new JsonArray(missing);
        if (fault == "tree-as-parent") body["parents"] = new JsonArray(tree);
        var counts = (server.Blobs.Count, server.Trees.Count, server.Commits.Count);
        using var response = await PostObject(client, "repos/Owner/repo/git/" + (commitFault ? "commits" : "trees"), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(counts, (server.Blobs.Count, server.Trees.Count, server.Commits.Count));
        Assert.Equal(server.BaseOid, server.Refs["refs/heads/main"]);
        Assert.Equal(0, server.SuccessfulCas);
        Assert.Empty(server.PullRequests);
    }

    [Fact]
    public async Task Git_object_fixture_accepts_reused_content_and_exact_existing_graph_references()
    {
        await using var server = new GitHubProposalLoopbackServer(new Dictionary<string, byte[]> { ["seed.txt"] = "seed"u8.ToArray() });
        using var client = ObjectClient(server);
        var blob = server.Blobs.Keys.Single();
        using var leaf = await PostObject(client, "repos/Owner/repo/git/trees", new
        {
            tree = new[]
        {
            new { path = "a.txt", mode = "100644", type = "blob", sha = blob },
            new { path = "b.txt", mode = "100755", type = "blob", sha = blob },
        }
        });
        Assert.Equal(HttpStatusCode.Created, leaf.StatusCode);
        var leafOid = (await leaf.Content.ReadFromJsonAsync<JsonObject>())!["sha"]!.GetValue<string>();
        using var root = await PostObject(client, "repos/Owner/repo/git/trees", new
        {
            tree = new[]
        {
            new { path = "App", mode = "040000", type = "tree", sha = leafOid },
        }
        });
        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var rootOid = (await root.Content.ReadFromJsonAsync<JsonObject>())!["sha"]!.GetValue<string>();
        using var commit = await PostObject(client, "repos/Owner/repo/git/commits", CommitBody(rootOid, server.BaseOid));
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        var commitOid = (await commit.Content.ReadFromJsonAsync<JsonObject>())!["sha"]!.GetValue<string>();
        Assert.Equal(rootOid, server.Commits[commitOid]["tree"]!["sha"]!.GetValue<string>());
        Assert.Equal(server.BaseOid, server.Commits[commitOid]["parents"]![0]!["sha"]!.GetValue<string>());
        Assert.Equal(2, server.Trees[leafOid].Length);
        Assert.All(server.Trees[leafOid], entry => Assert.Equal(blob, entry.Oid));
        Assert.Equal(server.BaseOid, server.Refs["refs/heads/main"]);
    }

    private static async Task<HttpResponseMessage> PostObject(HttpClient client, string path, object body)
    {
        using var content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new("application/json");
        return await client.PostAsync(path, content);
    }

    private static HttpClient ObjectClient(GitHubProposalLoopbackServer server)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = server.Endpoint };
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        client.DefaultRequestHeaders.Authorization = new("Bearer", Fixture.Placeholder);
        return client;
    }

    private static JsonObject CommitBody(string tree, string parent) => new()
    {
        ["tree"] = tree,
        ["parents"] = new JsonArray(parent),
        ["message"] = "Synthetic graph check\n",
        ["author"] = new JsonObject { ["name"] = "Synthetic", ["email"] = "synthetic@example.invalid", ["date"] = "2000-01-01T00:00:00Z" },
        ["committer"] = new JsonObject { ["name"] = "Synthetic", ["email"] = "synthetic@example.invalid", ["date"] = "2000-01-01T00:00:00Z" },
    };
}
