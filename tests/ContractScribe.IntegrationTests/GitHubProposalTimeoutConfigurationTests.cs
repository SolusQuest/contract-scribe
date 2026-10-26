using System.Diagnostics;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Terminal_five_member_durability_exceeds_provider_90s_without_resetting_its_original_window()
    {
        await using var fixture = await FiveTargetFixture("shared");
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["budgets"]!["invocation"] = new JsonObject
        {
            ["maximumTargets"] = 5,
            ["maximumProviderRequests"] = 8,
            ["maximumElapsedMilliseconds"] = 600000,
            ["maximumRequestElapsedMilliseconds"] = 90000,
            ["maximumConnectMilliseconds"] = 10000,
        };
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());

        long? terminalOrdinal = null, terminalStart = null, terminalEnd = null;
        var delayedRevisions = new HashSet<long>();
        fixture.GitHub.BeforeResponse = request =>
        {
            if (!fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal))) return null;
            var document = fixture.GitHub.CoordinationDocument();
            if (document["checkpoint"]?["oid"]?.GetValue<string>() is not { } checkpointOid) return null;
            var parsed = CampaignStateJson.Parse(fixture.GitHub.Blobs[checkpointOid]);
            Assert.True(parsed.IsValid);
            var artifact = parsed.Artifact!;
            var reservation = artifact.State.ActiveReservation as CampaignProviderReservation;
            if (terminalOrdinal is null && reservation?.HostPhase == CampaignProviderHostPhase.TerminalSubmission)
            {
                terminalOrdinal = reservation.CurrentOperationOrdinal;
                terminalStart = Stopwatch.GetTimestamp();
            }
            if (terminalOrdinal is null) return null;
            if (reservation?.HostPhase != CampaignProviderHostPhase.TerminalSubmission
                || reservation.CurrentOperationOrdinal != terminalOrdinal)
            {
                terminalEnd ??= Stopwatch.GetTimestamp();
                return null;
            }
            // The current coordination ref must already name this exact checkpoint.
            // Immutable object authentication before CAS cannot trigger the delay.
            if (request.Method != "GET"
                || !request.Path.EndsWith("/git/blobs/" + checkpointOid, StringComparison.Ordinal)
                || !delayedRevisions.Add(artifact.CheckpointRevision)) return null;
            return Task.Delay(TimeSpan.FromSeconds(15), fixture.GitHub.Stopping);
        };

        var result = await fixture.Run();
        var observedMilliseconds = terminalStart is { } start
            ? Stopwatch.GetElapsedTime(start, terminalEnd ?? Stopwatch.GetTimestamp()).TotalMilliseconds : 0;
        payloadOutput.WriteLine($"Terminal timing: outcomeExit={result.ExitCode}; ordinal={terminalOrdinal}; "
            + $"confirmationRevisions={delayedRevisions.Count}; observedMilliseconds={observedMilliseconds:F0}; "
            + $"providerRequestMaximumMilliseconds=90000; invocationMaximumMilliseconds=600000.");
        payloadOutput.WriteLine(result.Stdout);
        AssertResult(result, 0, "published");
        Assert.NotNull(terminalStart);
        Assert.NotNull(terminalEnd);
        Assert.Equal(7, delayedRevisions.Count); // admission, five members, one progress acknowledgement
        Assert.InRange(observedMilliseconds, 100000, 300000);
        var checkpoint = fixture.Checkpoint();
        Assert.True(checkpoint.State.ScribeProgress!.Session!.Ended);
        Assert.Equal(5, checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        Assert.All(checkpoint.State.ScribeProgress.Members, member => Assert.NotNull(member.ContentSha256));
        Assert.Null(checkpoint.State.ActiveReservation);
        Assert.Equal(1, checkpoint.State.LineageCharges.PatchValidationInvocations);
        Assert.Single(fixture.GitHub.PullRequests);
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: "published");
        await fixture.AssertSourceUnchanged();
    }
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_deadline_during_fifth_member_or_progress_readback_preserves_the_acknowledged_Writer(bool progressReadback)
    {
        await using var fixture = await FiveTargetFixture("shared");
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["timeouts"] = new JsonObject { ["terminalSubmissionMilliseconds"] = 2000 };
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());
        long? interruptedRevision = null;
        string? interruptedCheckpointOid = null;
        var failureIndex = 0;
        var cleanupCas = 0;
        fixture.GitHub.BeforeResponse = request =>
        {
            if (interruptedRevision is not null)
            {
                if (request.Method == "POST" && request.Path == "/graphql")
                {
                    cleanupCas++;
                    Assert.Equal(interruptedCheckpointOid, fixture.GitHub.CoordinationDocument()["checkpoint"]!["oid"]!.GetValue<string>());
                }
                return null;
            }
            if (request.Method != "GET"
                || !fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal))) return null;
            var document = fixture.GitHub.CoordinationDocument();
            if (document["checkpoint"]?["oid"]?.GetValue<string>() is not { } oid
                || !request.Path.EndsWith("/git/blobs/" + oid, StringComparison.Ordinal)) return null;
            var current = CampaignStateJson.Parse(fixture.GitHub.Blobs[oid]).Artifact!;
            var progress = current.State.ScribeProgress;
            if (current.State.ActiveReservation is not CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.TerminalSubmission }
                || progress?.Members.Count(member => member.ContentSha256 is not null) != 5
                || (progress.LastProgressSha256 is not null) != progressReadback) return null;
            interruptedRevision = current.CheckpointRevision;
            interruptedCheckpointOid = oid;
            failureIndex = fixture.GitHub.Requests.Count;
            return Task.Delay(TimeSpan.FromSeconds(3), fixture.GitHub.Stopping);
        };
        var result = await fixture.Run();
        AssertResult(result, 7, "timeout");
        Assert.NotNull(interruptedRevision);
        using var envelope = System.Text.Json.JsonDocument.Parse(result.Stdout);
        Assert.Equal(interruptedRevision - 1, envelope.RootElement.GetProperty("checkpointRevision").GetInt64());
        var actual = fixture.Checkpoint();
        Assert.Equal(interruptedRevision, actual.CheckpointRevision);
        Assert.Equal(5, actual.State.ScribeProgress!.Members.Count(member => member.ContentSha256 is not null));
        Assert.False(actual.State.ScribeProgress.Session!.Ended);
        Assert.NotNull(actual.State.ActiveReservation);
        Assert.Null(actual.State.CandidateObservation);
        Assert.Equal(2, fixture.Provider.RequestCount);
        Assert.True(failureIndex > 0);
        Assert.Equal(1, cleanupCas); // Mandatory claim shortening, with no new checkpoint or publication grant.
        Assert.True(fixture.GitHub.CoordinationDocument()["claim"]!["expiresUnixSeconds"]!.GetValue<long>()
            <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Equal(0, actual.State.LineageCharges.PatchValidationInvocations);
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: "timeout");
        await fixture.AssertSourceUnchanged();
    }

}
