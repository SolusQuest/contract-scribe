using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Closed_unmerged_successor_requires_exact_explicit_authorization_and_replays_it_across_processes()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        fixture.GitHub.Lifecycle("closed");
        AssertResult(await fixture.Run("resume"), 4, "closed-unmerged");
        var closed = fixture.GitHub.PullRequests[0];
        fixture.Snapshot = "snapshot.github.successor";
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.GitHubConfiguration))!;
        config["operationId"] = "operation.successor";
        config["generationId"] = "generation.successor";
        config["transition"] = "successor-after-closed-unmerged";
        config["terminalPredecessor"] = new JsonObject
        {
            ["logicalPredecessorId"] = "predecessor.closed",
            ["pullRequestNumber"] = 1,
            ["generationId"] = "generation.initial",
            ["headOid"] = closed["head"]!["sha"]!.DeepClone(),
            ["disposition"] = "closed-unmerged",
        };
        await File.WriteAllTextAsync(fixture.GitHubConfiguration, config.ToJsonString());
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.Mutations;
        // A real fresh M4 candidate can be accepted locally, but absence of the explicit
        // authorization still stops H1 before any credential read or GitHub request.
        AssertResult(await fixture.Run("resume"), 4, "local-invalid");
        Assert.Equal(reads, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.Mutations);
        var fresh = fixture.Checkpoint();
        config["closedUnmergedSuccessorAuthorization"] = new JsonObject
        {
            ["authorizationId"] = "authorization.explicit",
            ["logicalPredecessorId"] = "predecessor.closed",
            ["closedPullRequestNumber"] = 1,
            ["closedGenerationId"] = "generation.initial",
            ["closedHeadOid"] = closed["head"]!["sha"]!.DeepClone(),
            ["freshSnapshotCommitmentSha256"] = GitHubPublicationRequestFactory.CreateSnapshotCommitment(fresh.State.Snapshot),
            ["freshWorkPlanCommitmentSha256"] = fresh.State.Snapshot.ExecutionCommitmentSha256,
            ["freshCandidateCommitmentSha256"] = fresh.State.AcceptedCandidateOrigin!.CandidateObservation.PatchResultCommitmentSha256,
            ["newGenerationId"] = "generation.successor",
            ["operationId"] = "operation.successor",
        };
        await File.WriteAllTextAsync(fixture.GitHubConfiguration, config.ToJsonString());
        AssertResult(await fixture.Run("resume"), 0, "published");
        Assert.Equal(2, fixture.GitHub.PullRequests.Count);
        Assert.Single(fixture.GitHub.PullRequests, pr => pr["state"]!.GetValue<string>() == "open");
        writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Policy_change_holds_the_verified_predecessor_with_its_generation_and_no_attempted_operation_id()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        await ConfigureAppend(fixture);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.GitHubConfiguration))!;
        config["policy"]!["maximumDocumentationBlocks"] = 127;
        config["appendPredecessor"]!["policyCommitmentSha256"] = GitHubPublicationCommitments.CreatePolicy(
            new(128, 128, 4194304), new(127, 128, 4194304));
        await File.WriteAllTextAsync(fixture.GitHubConfiguration, config.ToJsonString());
        var writes = fixture.GitHub.Mutations;
        var result = await fixture.Run("resume");
        AssertResult(result, 0, "awaiting-review");
        Assert.Equal(writes, fixture.GitHub.Mutations);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publicationOperationId").ValueKind);
        Assert.Equal("generation.initial", json.RootElement.GetProperty("generationId").GetString());
        Assert.Equal("https://github.com/Owner/repo/pull/1", json.RootElement.GetProperty("pullRequestUrl").GetString());
    }

    [Fact]
    public async Task Target_movement_records_stale_draft_and_restoring_the_original_base_cannot_regain_eligibility()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        lock (fixture.GitHub.Gate)
        {
            var basis = fixture.GitHub.Commits[fixture.GitHub.BaseOid];
            var moved = fixture.GitHub.AddCommit(new JsonObject
            {
                ["tree"] = basis["tree"]!["sha"]!.DeepClone(),
                ["parents"] = new JsonArray(fixture.GitHub.BaseOid),
                ["message"] = "Synthetic target movement\n",
                ["author"] = basis["author"]!.DeepClone(),
                ["committer"] = basis["committer"]!.DeepClone(),
            });
            fixture.GitHub.Before = request =>
            {
                if (request.Method == "POST" && request.Kind == "pulls")
                    fixture.GitHub.Refs["refs/heads/main"] = moved;
            };
        }
        var stale = await fixture.Run("start");
        AssertResult(stale, 3, "stale-base-after-create");
        using var json = JsonDocument.Parse(stale.Stdout);
        Assert.Equal("operation.initial", json.RootElement.GetProperty("publicationOperationId").GetString());
        Assert.Equal("generation.initial", json.RootElement.GetProperty("generationId").GetString());
        fixture.GitHub.Before = null;
        fixture.GitHub.Refs["refs/heads/main"] = fixture.GitHub.BaseOid;
        var writes = fixture.GitHub.Mutations;
        var restored = await fixture.Run("resume");
        AssertResult(restored, 3, "conflict");
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertResult(await fixture.Run("resume"), 3, "conflict");
        Assert.Equal(writes, fixture.GitHub.Mutations);
    }
}
