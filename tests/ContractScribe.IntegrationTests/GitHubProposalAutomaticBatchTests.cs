using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Automatic_no_result_batch_advances_exact_deferred_members_and_preserves_closed_history()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4, providerScenario: "closed-proposal");
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 0, "no-op");
        var before = fixture.Checkpoint();
        Assert.Equal(2, before.State.Batch.SelectedTargetKeys.Length);
        Assert.Equal(2, before.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(2, before.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        var after = fixture.Checkpoint();
        Assert.Equal(before.State.Snapshot, after.State.Snapshot);
        Assert.NotEqual(before.State.Batch.Identity, after.State.Batch.Identity);
        Assert.Empty(before.State.Batch.SelectedTargetKeys.Intersect(after.State.Batch.SelectedTargetKeys));
        Assert.Equal(4, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        foreach (var work in before.State.WorkItems.Where(item => item.OuterAttemptCount > 0))
            Assert.Equal(work, after.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey));
        Assert.True(after.State.LineageCharges.ProviderRequests.TotalCharged > before.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.True(fixture.Provider.RequestCount > requests);
        var finalRequests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        Assert.Equal(finalRequests, fixture.Provider.RequestCount);
        Assert.Equal(after.Sha256, fixture.Checkpoint().Sha256);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Automatic_zero_quota_stays_empty_then_positive_quota_admits_initial_members()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 3);
        await SetAutomaticQuota(fixture, 0);
        AssertResult(await fixture.Run(), 0, "no-op");
        var empty = fixture.Checkpoint();
        AssertResult(await fixture.Run(), 0, "no-op");
        Assert.Equal(empty.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(0, fixture.Provider.RequestCount);
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 0, "published");
        var next = fixture.Checkpoint();
        Assert.Equal(empty.State.Snapshot, next.State.Snapshot);
        Assert.Equal(2, next.State.Batch.SelectedTargetKeys.Length);
        Assert.Equal(2, next.State.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
    }

    private static async Task SetAutomaticQuota(Fixture fixture, int maximumTargets)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        config["budgets"]!["invocation"] = new JsonObject { ["maximumTargets"] = maximumTargets, ["maximumProviderRequests"] = 64 };
        await File.WriteAllTextAsync(fixture.Configuration!, config.ToJsonString());
    }
}
