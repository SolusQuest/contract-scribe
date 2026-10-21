using System.Reflection;
using System.Text.Json;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Closed_same_base_precedes_configuration_changes_and_new_HEAD_allows_fresh_Audit_without_successor_approval()
    {
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var prior = fixture.Checkpoint();
        fixture.GitHub.Lifecycle("closed");
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["campaign"]!["maximumElapsedMilliseconds"] = 7_200_000;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        fixture.Snapshot = "changed-config-snapshot";
        var before = GateCounts(fixture);
        var paused = await fixture.Run("resume");
        AssertResult(paused, 4, "closed-unmerged");
        Assert.Contains("closed-same-base", paused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, GateCounts(fixture));
        Assert.Equal(prior.Sha256, fixture.Checkpoint().Sha256);
        fixture.GitHub.PullRequests[0]["base"]!["sha"] = AdvanceReadme(fixture);
        AssertResult(await fixture.Run("resume"), 0, "no-op");
        var current = fixture.Checkpoint();
        Assert.NotEqual(prior.State.Snapshot, current.State.Snapshot);
        Assert.Null(current.State.CandidateObservation);
        Assert.Equal(prior.State.LineageCharges, current.State.LineageCharges);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Correctness_change_holds_the_verified_predecessor_with_its_generation_and_no_attempted_operation_id()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        await ConfigureSealedResume(fixture);
        var changed = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        changed["planning"] = new JsonObject { ["targetProfile"] = "profile.assembly-visible" };
        await File.WriteAllTextAsync(fixture.Configuration!, changed.ToJsonString());
        var writes = fixture.GitHub.PublicationWrites;
        var result = await fixture.Run("resume");
        AssertResult(result, 0, "awaiting-review");
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
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
        var writes = fixture.GitHub.PublicationWrites;
        var restored = await fixture.Run("resume");
        AssertResult(restored, 4, "stale");
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertResult(await fixture.Run("resume"), 4, "stale");
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
    }
    private static async Task ArchiveRemoteCheckpoint(Fixture fixture)
    {
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var request = GitHubProposalRequestReader.Read(arguments.Request, fixture.Repository.Root).Request;
        var github = request.GitHub;
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [fixture.GitHub.Endpoint, null, 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            github.RepositoryOwner, github.RepositoryName, github.TargetRef, request.CampaignLineage,
            github.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        await using var checkpoint = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await checkpoint.AcquireCheckpointAsync(default));
        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(await checkpoint.ReadAsync(default));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var transition = CampaignStateReducer.ArchiveDispatchHistory(accepted.AcceptedCheckpoint!);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted,
            (await CampaignCheckpointAcceptance.AcceptAsync(checkpoint, transition, default)).Kind);
    }

}
