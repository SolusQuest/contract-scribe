using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubApiClientTests
{
    [Theory]
    [InlineData("graphql", true)]
    [InlineData("rest", true)]
    [InlineData("different-id", false)]
    [InlineData("different-node", false)]
    [InlineData("user-kind", false)]
    [InlineData("organization-kind", false)]
    [InlineData("other-login", false)]
    [InlineData("missing-author", false)]
    public async Task Campaign_actor_keeps_raw_login_and_only_matches_the_known_bot_identity(string variant, bool matches)
    {
        using var harness = await Harness.Create();
        var actor = new JsonObject
        {
            ["__typename"] = "Bot",
            ["databaseId"] = 41898282,
            ["id"] = "MDM6Qm90NDE4OTgyODI=",
            ["login"] = "github-actions"
        };
        if (variant == "rest") actor["login"] = "github-actions[bot]";
        if (variant == "different-id") actor["databaseId"] = 41898283;
        if (variant == "different-node") actor["id"] = "BOT_other";
        if (variant == "user-kind") actor["__typename"] = "User";
        if (variant == "organization-kind") actor["__typename"] = "Organization";
        if (variant == "other-login") actor["login"] = "another-bot";
        harness.Handler.Reply = (_, _) =>
        {
            var page = SnapshotPage(1);
            var connection = page["data"]!["repository"]!["pullRequests"]!;
            connection["totalCount"] = 1;
            connection["pageInfo"]!["hasNextPage"] = false;
            connection["nodes"]![0]!["author"] = variant == "missing-author" ? null : actor.DeepClone();
            return Task.FromResult(Json(page));
        };
        var result = await harness.Client.ReadCampaignSnapshotAsync(false, CancellationToken.None);
        var actual = Assert.Single(Assert.IsType<GitHubCampaignSnapshot>(result.Value).PullRequests.Items).Author;
        Assert.Equal(matches, GitHubPublicationPrincipal.SameActor(actual, GitHubPublicationPrincipal.ActionsBot));
        Assert.Equal(matches, GitHubPublicationPrincipal.SameActor(GitHubPublicationPrincipal.ActionsBot, actual));
        if (actual is not null) Assert.Equal(actor["login"]!.GetValue<string>(), actual.Login);
        Assert.Single(harness.Handler.Requests); // No identity lookup is added.
    }

    [Theory]
    [InlineData("none")]
    [InlineData("partial-errors")]
    [InlineData("missing-field")]
    [InlineData("missing-target")]
    [InlineData("missing-repository")]
    [InlineData("ref-node-drift")]
    [InlineData("repository-drift")]
    [InlineData("permission-drift")]
    [InlineData("total-drift")]
    [InlineData("incomplete")]
    [InlineData("duplicate-changed")]
    [InlineData("duplicate-number")]
    [InlineData("duplicate-node")]
    [InlineData("cursor-repeat")]
    [InlineData("cursor-missing")]
    [InlineData("cursor-oversize")]
    [InlineData("unknown-actor")]
    [InlineData("base-mismatch")]
    [InlineData("http-forbidden")]
    [InlineData("malformed-ref-node")]
    [InlineData("malformed-actor-node")]
    [InlineData("malformed-pr-node")]
    public async Task Campaign_snapshot_keeps_actual_permission_vector_and_rejects_incomplete_or_changed_pages(string fault)
    {
        using var harness = await Harness.Create();
        var calls = 0;
        var permissions = Repository();
        permissions["permissions"] = new JsonObject
        {
            ["admin"] = false,
            ["push"] = true,
            ["pull"] = true,
            ["triage"] = false
        };
        harness.Handler.Reply = async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Json(permissions);
            await CaptureBody(request, token);
            request.Options.TryGetValue(BodyKey, out var body);
            var query = JsonNode.Parse(body!)!;
            Assert.Equal(GitHubApiClient.CampaignSnapshotDocument, query["query"]!.GetValue<string>());
            Assert.Equal(calls == 0 ? null : "page-one", query["variables"]!["after"]?.GetValue<string>());
            if (fault == "http-forbidden") return Json(new JsonObject { ["message"] = "synthetic denied" }, (int)HttpStatusCode.Forbidden);
            var page = SnapshotPage(calls++ == 0 ? 1 : 2);
            var repo = page["data"]!["repository"]!;
            var connection = repo["pullRequests"]!;
            var node = connection["nodes"]![0]!;
            if (fault == "partial-errors") page["errors"] = new JsonArray(new JsonObject { ["message"] = "synthetic partial" });
            if (fault == "missing-field") node.AsObject().Remove("body");
            if (fault == "missing-target") repo["base"] = null;
            if (fault == "missing-repository") page["data"]!["repository"] = null;
            if (fault == "cursor-missing") connection["pageInfo"]!["endCursor"] = null;
            if (fault == "cursor-oversize") connection["pageInfo"]!["endCursor"] = new string('c', 2049);
            if (fault == "unknown-actor") node["author"]!["__typename"] = "EnterpriseUserAccount";
            if (fault == "base-mismatch") node["baseRepository"]!["id"] = "R_other";
            if (fault == "malformed-ref-node") repo["base"]!["id"] = "\nnot-a-node";
            if (fault == "malformed-actor-node") node["author"]!["id"] = "\nnot-a-node";
            if (fault == "malformed-pr-node") node["id"] = "\nnot-a-node";
            if (calls == 2)
            {
                if (fault == "ref-node-drift") repo["base"]!["id"] = "REF_recreated";
                if (fault == "repository-drift") repo["id"] = "R_recreated";
                if (fault == "permission-drift") repo["viewerPermission"] = "READ";
                if (fault == "total-drift") connection["totalCount"] = 3;
                if (fault == "incomplete") connection["totalCount"] = 1;
                if (fault == "duplicate-changed")
                {
                    connection["nodes"]![0] = SnapshotPull(1);
                    connection["nodes"]![0]!["body"] = "changed full body";
                }
                if (fault == "duplicate-number") node["number"] = 1;
                if (fault == "duplicate-node") node["id"] = "PR_1";
                if (fault == "cursor-repeat")
                {
                    connection["pageInfo"]!["hasNextPage"] = true;
                    connection["pageInfo"]!["endCursor"] = "page-one";
                }
            }
            var response = Json(page);
            response.Headers.Date = DateTimeOffset.Parse("2026-10-30T00:00:00Z", CultureInfo.InvariantCulture);
            return response;
        };
        var result = await harness.Client.ReadCampaignSnapshotAsync(true, CancellationToken.None);
        Assert.All(harness.Handler.Requests, request => Assert.True(request.Method is "GET" or "POST"));
        Assert.Single(harness.Handler.Requests, request => request.Method == "GET");
        Assert.InRange(harness.Handler.Requests.Count, 2, 3);
        if (fault != "none")
        {
            Assert.Null(result.Value);
            Assert.NotNull(result.Failure);
            Assert.Equal(GitHubDelivery.Read, result.Delivery);
            return;
        }
        var snapshot = Assert.IsType<GitHubCampaignSnapshot>(result.Value);
        Assert.Null(result.Failure);
        Assert.Equal(new GitHubRolePermissions(false, true, true, null, false), snapshot.PermissionSample!.RolePermissions);
        Assert.Null(snapshot.Repository.ViewerPermission);
        Assert.Equal(2, snapshot.PullRequests.Pages);
        Assert.Equal(2, snapshot.PullRequests.ObservedItems);
        Assert.Equal(2, snapshot.PullRequests.Items.Length);
        Assert.True(snapshot.PullRequests.Exhausted);
        Assert.True(snapshot.PullRequests.BodyBytes > 0);
        Assert.All(snapshot.PullRequests.Items, pr =>
        {
            Assert.False(pr.MaintainerCanModify);
            Assert.False(pr.Merged);
            Assert.NotNull(pr.Author);
            Assert.Equal("full marker body", pr.Body);
        });
    }

    [Fact]
    public async Task Campaign_snapshot_closing_is_a_distinct_read_and_cancellation_never_reuses_opening_data()
    {
        using var harness = await Harness.Create();
        var queryCalls = 0;
        harness.Handler.Reply = async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Json(Repository());
            await CaptureBody(request, token);
            var page = SnapshotPage(1);
            var connection = page["data"]!["repository"]!["pullRequests"]!;
            connection["totalCount"] = 1;
            connection["pageInfo"]!["hasNextPage"] = false;
            if (++queryCalls > 1) connection["nodes"]![0]!["body"] = "changed after opening";
            return Json(page);
        };
        var opening = await harness.Client.ReadCampaignSnapshotAsync(true, CancellationToken.None);
        var closing = await harness.Client.ReadCampaignSnapshotAsync(false, CancellationToken.None);
        Assert.NotNull(opening.Value);
        Assert.NotNull(closing.Value);
        Assert.Single(harness.Handler.Requests, request => request.Method == "GET");
        Assert.Equal(2, queryCalls);
        Assert.NotEqual(opening.Value.PullRequests.Items[0], closing.Value.PullRequests.Items[0]);
        Assert.Null(closing.Value.PermissionSample);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = await harness.Client.ReadCampaignSnapshotAsync(false, cancelled.Token);
        Assert.Null(stopped.Value);
        Assert.Equal(2, queryCalls);
        Assert.Equal(GitHubFailureCode.Cancelled, stopped.Failure!.Code);
    }

    private static JsonObject SnapshotPage(int page) => new()
    {
        ["data"] = new JsonObject
        {
            ["repository"] = new JsonObject
            {
                ["id"] = "R_42",
                ["fullDatabaseId"] = "42",
                ["name"] = "repo",
                ["owner"] = SnapshotActor(),
                ["isPrivate"] = true,
                ["isArchived"] = false,
                ["isDisabled"] = false,
                ["viewerPermission"] = null,
                ["base"] = SnapshotRef("main", Oid('1')),
                ["coord"] = null,
                ["pullRequests"] = new JsonObject
                {
                    ["totalCount"] = 2,
                    ["nodes"] = new JsonArray(SnapshotPull(page)),
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = page == 1, ["endCursor"] = page == 1 ? "page-one" : "page-two" }
                }
            }
        }
    };
    private static JsonObject SnapshotActor() => new() { ["__typename"] = "User", ["databaseId"] = 7, ["id"] = "U_7", ["login"] = "Owner" };
    private static JsonObject SnapshotRepo() => new()
    { ["id"] = "R_42", ["fullDatabaseId"] = "42", ["name"] = "repo", ["owner"] = SnapshotActor() };
    private static JsonObject SnapshotRef(string name, string oid) => new()
    {
        ["id"] = "REF_main",
        ["prefix"] = "refs/heads/",
        ["name"] = name,
        ["target"] = new JsonObject { ["__typename"] = "Commit", ["oid"] = oid }
    };
    private static JsonObject SnapshotPull(int number) => new()
    {
        ["id"] = "PR_" + number,
        ["fullDatabaseId"] = (1000 + number).ToString(CultureInfo.InvariantCulture),
        ["number"] = number,
        ["state"] = "OPEN",
        ["isDraft"] = true,
        ["merged"] = false,
        ["mergedAt"] = null,
        ["closedAt"] = null,
        ["createdAt"] = "2026-09-03T08:00:00Z",
        ["title"] = "complete title",
        ["body"] = "full marker body",
        ["maintainerCanModify"] = false,
        ["headRefName"] = "proposal",
        ["headRefOid"] = Oid('2'),
        ["baseRefName"] = "main",
        ["baseRefOid"] = Oid('1'),
        ["headRepository"] = SnapshotRepo(),
        ["baseRepository"] = SnapshotRepo(),
        ["author"] = SnapshotActor()
    };
}
