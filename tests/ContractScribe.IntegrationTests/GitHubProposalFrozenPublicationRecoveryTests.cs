using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Closed_unmerged_different_base_publishes_a_fresh_Audit_generation_without_an_extra_unlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run(), 0, "published");
        var previous = fixture.GitHub.Coordination();
        fixture.GitHub.Lifecycle("closed");
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 4, "closed-unmerged");
        Assert.Equal(requests, fixture.Provider.RequestCount);
        AdvanceReadme(fixture);
        AssertResult(await fixture.Run(), 0, "published");
        Assert.Equal(2, fixture.GitHub.PrAttempts);
        Assert.Equal(2, fixture.GitHub.PullRequests.Count);
        Assert.Single(fixture.GitHub.PullRequests, pr => pr["state"]!.GetValue<string>() == "open");
        Assert.NotEqual(previous["batchIdentity"]!.GetValue<string>(), fixture.Checkpoint().State.Batch.Identity);
        Assert.True(fixture.Provider.RequestCount > requests);
        Assert.NotEqual(previous["generationId"]!.GetValue<string>(), fixture.GitHub.Coordination()["generationId"]!.GetValue<string>());
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("claimed")]
    [InlineData("content-created")]
    [InlineData("proposal-ref-advanced")]
    [InlineData("pull-request-create-started")]
    public async Task Fresh_process_recovers_only_the_frozen_original_operation_after_durable_publication_boundary(string stage)
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 6, providerScenario: "shared");
        await SetAutomaticQuota(fixture, 5);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GitHub.After = request =>
        {
            if (request.Method != "POST" || request.Kind != "cas"
                || !fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal))
                || fixture.GitHub.CoordinationDocument()["publication"] is null
                || fixture.GitHub.Coordination()["stage"]!.GetValue<string>() != stage) return;
            // The actual durable CAS has happened; its acknowledgement is withheld.
            fixture.GitHub.DelayResponses = true;
            reached.TrySetResult();
        };
        JsonObject intent;
        using (var interrupted = CampaignCliProcessTests.Start(fixture.Args(), fixture.Environment()))
        {
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromMinutes(3));
                intent = fixture.GitHub.Coordination();
                Assert.Equal(stage, intent["stage"]!.GetValue<string>());
                Assert.Equal(fixture.Checkpoint().State.Batch.Identity, intent["batchIdentity"]!.GetValue<string>());
                Assert.Equal(5, fixture.Checkpoint().State.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
                Assert.Equal(5, fixture.Checkpoint().State.CandidateObservation!.AcceptedWorkItemKeys.Length);
                Assert.Equal(0, fixture.GitHub.PrAttempts);
            }
            finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        }
        fixture.GitHub.After = null;
        fixture.GitHub.DelayResponses = false;
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        var requests = fixture.Provider.RequestCount;
        var accepted = fixture.Checkpoint();
        await ConfigureSealedResume(fixture);
        var resumed = await fixture.Run();
        if (stage == "pull-request-create-started")
        {
            AssertResult(resumed, 3, "conflict");
            Assert.Empty(fixture.GitHub.PullRequests);
            Assert.Equal(0, fixture.GitHub.PrAttempts);
            Assert.Equal(stage, fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
            AssertResult(await fixture.Run(), 3, "conflict");
            Assert.Equal(0, fixture.GitHub.PrAttempts);
        }
        else
        {
            AssertResult(resumed, 0, "published");
            Assert.Single(fixture.GitHub.PullRequests);
            Assert.Equal(1, fixture.GitHub.PrAttempts);
            AssertResult(await fixture.Run(), 0, "awaiting-review");
            Assert.Equal(1, fixture.GitHub.PrAttempts);
        }
        var recovered = fixture.GitHub.Coordination();
        foreach (var key in new[] { "batchIdentity", "resultSetCommitmentSha256", "authorityCommitmentSha256",
            "operationId", "operationCommitmentSha256", "currentCandidateCommitmentSha256" })
            Assert.True(JsonNode.DeepEquals(intent[key], recovered[key]), key);
        Assert.Equal(requests, fixture.Provider.RequestCount);
        Assert.Equal(accepted.State.LineageCharges.ProviderRequests, fixture.Checkpoint().State.LineageCharges.ProviderRequests);
        Assert.Equal(accepted.State.LineageCharges.InputTokens, fixture.Checkpoint().State.LineageCharges.InputTokens);
        Assert.Equal(accepted.State.CandidateObservation!.AcceptedWorkItemKeys.ToArray(),
            fixture.Checkpoint().State.CandidateObservation!.AcceptedWorkItemKeys.ToArray());
        Assert.Null(fixture.Checkpoint().State.ActiveReservation);
        await fixture.AssertSourceUnchanged();
    }
}
