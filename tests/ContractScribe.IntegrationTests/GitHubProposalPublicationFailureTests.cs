using System.Text.Json;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(403, 4, "permission")]
    [InlineData(404, 3, "conflict")]
    public async Task Actual_original_POST_fence_failure_survives_CLI_and_Action_without_a_send(
        int status, int exit, string outcome)
    {
        await using var fixture = await Fixture.CreateAsync();
        var fencePrepared = false;
        var injected = false;
        fixture.GitHub.After = request =>
        {
            if (request.Method != "POST" || request.Kind != "commits") return;
            // Observe the actual prospective immutable fence objects, before its
            // durable CAS. This selects the producer without a request ordinal.
            foreach (var commit in fixture.GitHub.Commits)
            {
                var root = fixture.GitHub.Trees[commit.Value["tree"]!["sha"]!.GetValue<string>()];
                if (!root.Any(entry => entry.Path == ".contract-scribe")) continue;
                var document = fixture.GitHub.DocumentAt(commit.Key);
                if (document["publication"] is not { } publication) continue;
                using var evidence = JsonDocument.Parse(fixture.GitHub.Blobs[publication["oid"]!.GetValue<string>()]);
                if (evidence.RootElement.GetProperty("state").GetProperty("stage").GetString() == "pull-request-create-started")
                    fencePrepared = true;
            }
        };
        fixture.GitHub.Before = request =>
        {
            if (injected || !fencePrepared || request.Method != "GET" || request.Kind != "pulls") return;
            Assert.Equal("proposal-ref-advanced", fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
            fixture.GitHub.RejectPath = request.Path;
            fixture.GitHub.RejectionStatus = status;
            injected = true;
        };
        var failed = await fixture.Run();
        Assert.True(fencePrepared);
        Assert.True(injected);
        AssertResult(failed, exit, outcome);
        Assert.Equal("proposal-ref-advanced", fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
        Assert.NotNull(fixture.Checkpoint().State.CandidateObservation);
        Assert.Null(fixture.Checkpoint().State.ActiveReservation);
        Assert.Equal(1, fixture.Checkpoint().State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Empty(fixture.GitHub.PullRequests);
        using var envelope = JsonDocument.Parse(failed.Stdout);
        var diagnostic = envelope.RootElement.GetProperty("publicationDiagnostic");
        Assert.Equal("CoordinationAdvanceRef", diagnostic.GetProperty("boundary").GetString());
        Assert.Equal("Coordination", diagnostic.GetProperty("owner").GetString());
        Assert.Equal(status, diagnostic.GetProperty("transportHttpStatus").GetInt32());
        Assert.Equal(status == 403 ? "Permission" : "NotFound", diagnostic.GetProperty("transportCode").GetString());
        await AssertActualActionConsumer(fixture.Outside, failed.Stdout, failed.ExitCode, publication: true);
        await fixture.AssertSourceUnchanged();
    }
}
