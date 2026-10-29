using ContractScribe.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("target-drift")]
    [InlineData("collection-drift")]
    public async Task Campaign_snapshot_closing_rejects_drift_before_models_or_mutations(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bridge = new GitHubNativeLoopbackBridge(fixture.GitHub, fixture.Outside, deadline.Token);
        var queries = 0;
        var import = fixture.GitHub.Before;
        fixture.GitHub.Before = request =>
        {
            import?.Invoke(request);
            if (request.Kind != "campaign-snapshot" || ++queries != 2) return;
            if (fault == "target-drift")
            {
                var basis = fixture.GitHub.Commits[fixture.GitHub.BaseOid];
                fixture.GitHub.Refs["refs/heads/main"] = fixture.GitHub.AddCommit(new JsonObject
                {
                    ["tree"] = basis["tree"]!["sha"]!.DeepClone(),
                    ["parents"] = new JsonArray(fixture.GitHub.BaseOid),
                    ["message"] = "synthetic concurrent target\n",
                    ["author"] = basis["author"]!.DeepClone(),
                    ["committer"] = basis["committer"]!.DeepClone()
                });
            }
            else
            {
                var repository = new
                {
                    id = 1,
                    node_id = "REPO_node",
                    name = "repo",
                    full_name = "Owner/repo",
                    owner = new { id = 1, node_id = "OWNER_node", login = "Owner", type = "Organization" }
                };
                fixture.GitHub.PullRequests.Add(JsonSerializer.SerializeToNode(new
                {
                    id = 1,
                    node_id = "PR_external",
                    number = 1,
                    state = "closed",
                    draft = true,
                    merged = false,
                    merged_at = (string?)null,
                    closed_at = "2026-01-02T00:00:00Z",
                    created_at = "2026-01-01T00:00:00Z",
                    title = "unrelated synthetic PR",
                    body = "unrelated synthetic body",
                    maintainer_can_modify = false,
                    user = new { id = 1, node_id = "U_external", login = "external", type = "User" },
                    head = new { @ref = "unrelated", sha = fixture.GitHub.BaseOid, repo = repository },
                    @base = new { @ref = "main", sha = fixture.GitHub.BaseOid, repo = repository }
                })!.AsObject());
            }
        };
        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_GIT_ENDPOINT"] = bridge.Endpoint.AbsoluteUri;
        var result = await fixture.RunArgs(fixture.Args(), environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(2, queries);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.Mutations);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Default_hybrid_construction_runs_the_complete_two_target_CLI_with_separate_API_and_Git_counts()
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bridge = new GitHubNativeLoopbackBridge(fixture.GitHub, fixture.Outside, deadline.Token);
        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_GIT_ENDPOINT"] = bridge.Endpoint.AbsoluteUri;
        var result = await fixture.RunArgs(fixture.Args(), environment);
        payloadOutput.WriteLine("Complete default two-target CLI: direct REST/GraphQL attempts={0}; separate native Git HTTP requests={1}.",
            fixture.GitHub.Requests.Count, bridge.GitHttpRequests);
        // Closed synthetic request metadata only, never headers, packs or model
        // bodies. The local audit can account for every actual API attempt.
        payloadOutput.WriteLine("api-attempts=" + JsonSerializer.Serialize(fixture.GitHub.Requests.Select((request, index) =>
            new { index, request.Method, request.Path, request.Kind, request.Mutation, current = request.CurrentCheckpointQuery })));
        payloadOutput.WriteLine("Synthetic signed coordination records={0}.", fixture.GitHub.Commits.Values.Count(commit => commit["checkpointSignature"] is not null));
        payloadOutput.WriteLine("Campaign snapshot pages={0}; continuation pages={1}.",
            fixture.GitHub.Requests.Count(request => request.Kind == "campaign-snapshot"),
            fixture.GitHub.Requests.Count(request => request.Kind == "campaign-snapshot" && request.Body?["variables"]?["after"] is not null));
        AssertResult(result, 0, "published");
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.Equal(2, fixture.Checkpoint().State.WorkItems.Count(item => item.Status == CampaignWorkStatus.Accepted));
        await fixture.AssertSourceUnchanged();
    }
}
