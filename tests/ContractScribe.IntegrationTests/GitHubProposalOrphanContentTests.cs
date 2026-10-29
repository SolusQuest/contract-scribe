using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("human-pr")]
    [InlineData("target-change")]
    [InlineData("owner-change")]
    [InlineData("expiry")]
    [InlineData("attached-ref")]
    [InlineData("wrong-blob")]
    [InlineData("lost-before")]
    [InlineData("lost-after-unreadable")]
    public async Task Orphan_content_preparation_cannot_cross_a_changed_visibility_or_unconfirmed_object_boundary(string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        var server = fixture.GitHub;
        var injected = false;
        var firstUpload = -1;
        HashSet<string> previousBlobs = [];
        string? proposalRef = null;
        server.Before = request =>
        {
            if (injected || request.Method != "POST" || request.Kind != "blobs"
                || !server.Refs.Keys.Any(name => name.Contains("/coordination/", StringComparison.Ordinal))
                || server.CoordinationDocument()["publication"] is null
                || server.Coordination()["stage"]!.GetValue<string>() != "claimed") return;
            injected = true;
            firstUpload = server.Requests.Count - 1;
            previousBlobs = server.Blobs.Keys.ToHashSet(StringComparer.Ordinal);
            var document = server.CoordinationDocument();
            var state = server.Coordination();
            var read = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo",
                document["targetRef"]!.GetValue<string>(), document["campaignLineage"]!.GetValue<string>());
            proposalRef = GitHubPublicationFactory.CreateProposalRef(read, state["generationId"]!.GetValue<string>(),
                state["snapshotCommitmentSha256"]!.GetValue<string>(), state["batchIdentity"]!.GetValue<string>());
            if (fault is "lost-before" or "lost-after-unreadable")
            { server.LoseAt = request.Mutation; server.LoseAfter = fault == "lost-after-unreadable"; }
        };
        server.After = request =>
        {
            if (!injected || server.Requests.IndexOf(request) != firstUpload) return;
            var blob = server.Blobs.Keys.Except(previousBlobs, StringComparer.Ordinal).Single();
            if (fault == "wrong-blob") server.Blobs[blob] = "not the authenticated payload"u8.ToArray();
            if (fault == "lost-after-unreadable") server.RejectPath = "/git/blobs/" + blob;
            if (fault == "expiry") server.ServerNow = server.ServerNow.AddMinutes(6);
            if (fault == "owner-change") ReplaceOrphanOwner(server);
            if (fault == "attached-ref") server.Refs[proposalRef!] = server.BaseOid;
            if (fault == "target-change") AdvanceTarget(fixture,
                fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes), "Concurrent immutable target movement\n");
            if (fault == "human-pr")
            {
                var repository = new
                {
                    id = 1,
                    node_id = "REPO_node",
                    name = "repo",
                    full_name = "Owner/repo",
                    owner = new { id = 1, node_id = "OWNER_node", login = "Owner", type = "Organization" }
                };
                var campaign = server.Refs.Keys.Single(name => name.Contains("/coordination/", StringComparison.Ordinal)).Split('/')[^1];
                server.Refs["refs/heads/concurrent-human"] = server.BaseOid;
                server.PullRequests.Add(JsonSerializer.SerializeToNode(new
                {
                    id = 700,
                    node_id = "PR_700",
                    number = 700,
                    state = "open",
                    draft = true,
                    merged = false,
                    merged_at = (string?)null,
                    closed_at = (string?)null,
                    created_at = "2026-01-01T00:00:00Z",
                    title = "Concurrent human proposal",
                    body = "campaign=sha256:" + campaign,
                    user = new { id = 700, node_id = "USER_700", login = "human-reviewer", type = "User" },
                    head = new { @ref = "concurrent-human", sha = server.BaseOid, repo = repository },
                    @base = new { @ref = "main", sha = server.BaseOid, repo = repository },
                    maintainer_can_modify = false,
                })!.AsObject());
            }
        };
        var result = await fixture.Run();
        Assert.True(injected);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, server.PrAttempts);
        Assert.Equal("claimed", server.Coordination()["stage"]!.GetValue<string>());
        Assert.Equal(fault == "human-pr" ? 1 : 0, server.PullRequests.Count);
        if (fault == "attached-ref") Assert.Equal(server.BaseOid, server.Refs[proposalRef!]);
        else Assert.DoesNotContain(server.Refs.Keys, name => name.Contains("/proposals/", StringComparison.Ordinal));
        if (fault != "human-pr")
            Assert.DoesNotContain(server.Requests.Skip(firstUpload + 1), request => request.Method == "POST"
                && request.Kind is "blobs" or "trees" or "commits" or "cas" or "pulls");
        await fixture.AssertSourceUnchanged();
    }

    private static void ReplaceOrphanOwner(GitHubProposalLoopbackServer server)
    {
        var reference = server.Refs.Keys.Single(name => name.Contains("/coordination/", StringComparison.Ordinal));
        var before = server.Refs[reference];
        var root = server.Trees[server.Commits[before]["tree"]!["sha"]!.GetValue<string>()];
        var leaf = server.Trees[root.Single(entry => entry.Path == ".contract-scribe").Oid];
        var blob = leaf.Single(entry => entry.Path == "coordination-state-v1.json").Oid;
        var document = GitHubCoordinationDocument.Decode(server.Blobs[blob]).RetainAt(before);
        var prepared = GitHubCoordinationObjects.Prepare(document with
        {
            PredecessorOid = before,
            Claim = document.Claim! with { Owner = new string('e', 64), Epoch = document.Claim.Epoch + 1 },
        });
        server.AddBlob(prepared.StateBytes.ToArray());
        static GitHubProposalLoopbackServer.Entry Entry(GitHubTreeEntry entry) => new(entry.Path,
            entry.Mode == GitHubTreeMode.Directory ? "040000" : "100644", entry.Oid);
        server.AddTree(GitHubCoordinationObjects.LeafEntries(prepared).Select(Entry).ToArray());
        server.AddTree(GitHubCoordinationObjects.RootEntries(prepared).Select(Entry).ToArray());
        var commit = GitHubCoordinationObjects.CommitRequest(prepared);
        static JsonObject Actor(GitHubCommitActor actor) => new()
        { ["name"] = actor.Name, ["email"] = actor.Email, ["date"] = actor.Date.ToString("O") };
        var actual = server.AddCommit(new JsonObject
        {
            ["tree"] = commit.TreeOid,
            ["parents"] = new JsonArray(before),
            ["message"] = commit.Message,
            ["author"] = Actor(commit.Author),
            ["committer"] = Actor(commit.Committer),
        });
        Assert.Equal(prepared.CommitOid, actual);
        server.Refs[reference] = actual;
    }
}
