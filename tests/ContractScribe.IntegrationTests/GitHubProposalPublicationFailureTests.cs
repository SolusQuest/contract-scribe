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
        fixture.GitHub.BeforeBody = (request, body) =>
        {
            if (injected || request.Kind != "checkpoint-commit") return;
            var publication = GitHubProposalLoopbackServer.WrittenCanonicalFile(request, "publication-event.json");
            if (publication?["state"]?["stage"]?.GetValue<string>() != "pull-request-create-started") return;
            // This exact conditional commit is now the physical fence effect;
            // no prospective REST commit exists before it. Reject its actual
            // dispatch, preserve the original status, and require zero PR POST.
            fencePrepared = true;
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
