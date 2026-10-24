using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("shared", 0)]
    [InlineData("shared-skip-one", 1)]
    [InlineData("shared-skip-four", 4)]
    [InlineData("shared-closed", 5)]
    public async Task Five_target_progress_reaches_real_patch_publication_cleanup_and_Action(string scenario, int skips)
    {
        await using var fixture = await FiveTargetFixture(scenario);
        var result = await fixture.Run();
        var expected = skips == 5 ? "host-failure" : skips == 0 ? "published" : "published-partial";
        AssertResult(result, skips == 5 ? 5 : 0, expected);
        var checkpoint = fixture.Checkpoint();
        var progress = checkpoint.State.ScribeProgress!;
        Assert.Equal(5, progress.Members.Length);
        var skipped = progress.Members.Where(member => member.Skip is not null).ToArray();
        Assert.Equal(skips, skipped.Length);
        if (skips > 0)
        {
            Assert.Single(skipped.Select(member => member.ContentSha256).Distinct());
            Assert.All(skipped, member => Assert.Empty(member.Skip!.Evidence));
        }
        Assert.True(progress.Session!.Ended);
        Assert.Null(checkpoint.State.ActiveReservation);
        Assert.InRange(fixture.Provider.RequestCount, 1, 8);
        using var envelope = JsonDocument.Parse(result.Stdout);
        var usage = envelope.RootElement.GetProperty("invocationUsage");
        Assert.Equal(5, usage.GetProperty("work").GetProperty("selectedTargets").GetInt32());
        Assert.Equal(5 - skips, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
        Assert.Equal(skips, usage.GetProperty("work").GetProperty("skippedTargets").GetInt32());
        Assert.Equal(fixture.Provider.RequestCount, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("retryDispatches").GetInt32());
        if (skips == 5)
        {
            Assert.Contains("campaign.unresolved", envelope.RootElement.GetProperty("diagnosticCodes").EnumerateArray().Select(code => code.GetString()));
            Assert.Equal(0, checkpoint.State.LineageCharges.PatchValidationInvocations);
            Assert.Null(checkpoint.State.CandidateObservation);
            Assert.Empty(fixture.GitHub.PullRequests);
            Assert.Equal(0, fixture.GitHub.PrAttempts);
        }
        else
        {
            Assert.Equal(1, checkpoint.State.LineageCharges.PatchValidationInvocations);
            Assert.Equal(5 - skips, checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
            var pr = Assert.Single(fixture.GitHub.PullRequests);
            Assert.Equal(41898282, pr["user"]!["id"]!.GetValue<int>());
            Assert.True(pr["draft"]!.GetValue<bool>());
            Assert.False(pr["maintainer_can_modify"]!.GetValue<bool>());
            Assert.Equal(fixture.GitHub.BaseOid, pr["base"]!["sha"]!.GetValue<string>());
            var tree = fixture.GitHub.Commits[pr["head"]!["sha"]!.GetValue<string>()]["tree"]!["sha"]!.GetValue<string>();
            tree = fixture.GitHub.Trees[tree].Single(entry => entry.Path == "App").Oid;
            var blob = fixture.GitHub.Trees[tree].Single(entry => entry.Path == "App.cs").Oid;
            var patched = Encoding.UTF8.GetString(fixture.GitHub.Blobs[blob]);
            var original = Encoding.UTF8.GetString(fixture.Source);
            Assert.Equal(5 - skips, Regex.Matches(patched, "<summary>").Count - Regex.Matches(original, "<summary>").Count);
            static string WithoutDocumentation(string text) => Regex.Replace(text, @"(?m)^[ \t]*///[^\r\n]*(?:\r?\n|$)", "");
            Assert.Equal(WithoutDocumentation(original), WithoutDocumentation(patched));
            foreach (Match block in Regex.Matches(patched, @"(?m)(?:^[ \t]*///[^\r\n]*\r?\n)+"))
                _ = XDocument.Parse("<doc>" + Regex.Replace(block.Value, @"(?m)^[ \t]*/// ?", "") + "</doc>");
            Assert.True(fixture.GitHub.CoordinationDocument()["claim"]!["expiresUnixSeconds"]!.GetValue<long>()
                <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        }
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: expected);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Four_durable_identical_skips_keep_CP8_when_the_first_document_commit_is_rejected()
    {
        await using var fixture = await FiveTargetFixture("shared-skip-four");
        var failureIndex = -1;
        CampaignCheckpointArtifact? retained = null;
        fixture.GitHub.BeforeBody = (request, body) =>
        {
            if (failureIndex >= 0 || request.Method != "POST" || request.Path != "/repos/Owner/repo/git/commits") return;
            var next = JsonNode.Parse(body)!;
            var root = fixture.GitHub.Trees[next["tree"]!.GetValue<string>()];
            var directory = root.SingleOrDefault(entry => entry.Path == ".contract-scribe");
            if (directory is null) return;
            var blob = fixture.GitHub.Trees[directory.Oid].SingleOrDefault(entry => entry.Path == "checkpoint.json");
            if (blob is null || fixture.GitHub.CoordinationDocument()["checkpoint"] is null) return;
            var current = fixture.Checkpoint();
            var members = current.State.ScribeProgress?.Members ?? [];
            var successor = CampaignStateJson.Parse(fixture.GitHub.Blobs[blob.Oid]).Artifact!;
            if (members.Length != 5 || members.Count(member => member.Skip is not null) != 4
                || members.Any(member => member.Skip is null && member.ContentSha256 is not null)
                || successor.State.ScribeProgress?.Members.Any(member => member.Skip is null && member.ContentSha256 is not null) != true) return;
            retained = current;
            failureIndex = fixture.GitHub.Requests.Count;
            fixture.GitHub.RejectPath = request.Path;
            fixture.GitHub.RejectionStatus = 422;
        };
        var result = await fixture.Run();
        AssertResult(result, 5, "host-failure");
        Assert.NotNull(retained);
        Assert.Equal(8, retained.CheckpointRevision);
        Assert.Equal(retained.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(4, retained.State.ScribeProgress!.Members.Count(member => member.Skip is not null));
        Assert.Null(Assert.Single(retained.State.ScribeProgress.Members, member => member.Skip is null).ContentSha256);
        Assert.False(retained.State.ScribeProgress.Session!.Ended);
        Assert.NotNull(retained.State.ActiveReservation);
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.Equal(0, retained.State.LineageCharges.PatchValidationInvocations);
        using var envelope = JsonDocument.Parse(result.Stdout);
        Assert.Equal(8, envelope.RootElement.GetProperty("checkpointRevision").GetInt64());
        var failure = envelope.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal(9, failure.GetProperty("attemptedRevision").GetInt64());
        Assert.Equal("CreateObjects", failure.GetProperty("stage").GetString());
        Assert.Equal("Validation", failure.GetProperty("transportCode").GetString());
        Assert.Equal(422, failure.GetProperty("httpStatus").GetInt32());
        AssertNoPublicationAfterFault(fixture, failureIndex);
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: "host-failure");
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(403, "Permission")]
    [InlineData(404, "NotFound")]
    public async Task Five_target_required_current_document_read_cannot_reuse_an_immutable_cached_body(int status, string code)
    {
        await using var fixture = await FiveTargetFixture("shared");
        var failureIndex = -1;
        fixture.GitHub.Before = request =>
        {
            if (failureIndex >= 0 || fixture.Provider.RequestCount == 0 || request.Method != "GET") return;
            var head = fixture.GitHub.Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal)).Value;
            var root = fixture.GitHub.Trees[fixture.GitHub.Commits[head]["tree"]!["sha"]!.GetValue<string>()];
            var leaf = fixture.GitHub.Trees[root.Single(entry => entry.Path == ".contract-scribe").Oid];
            var state = leaf.Single(entry => entry.Path == "coordination-state-v1.json").Oid;
            if (!request.Path.EndsWith("/git/blobs/" + state, StringComparison.Ordinal)) return;
            failureIndex = fixture.GitHub.Requests.Count;
            fixture.GitHub.RejectPath = request.Path;
            fixture.GitHub.RejectionStatus = status;
        };
        var result = await fixture.Run();
        AssertResult(result, 4, "local-invalid");
        using var envelope = JsonDocument.Parse(result.Stdout);
        var failure = envelope.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal("PredecessorRead", failure.GetProperty("stage").GetString());
        Assert.Equal(code, failure.GetProperty("transportCode").GetString());
        Assert.Equal(status, failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal(1, fixture.Provider.RequestCount);
        AssertNoPublicationAfterFault(fixture, failureIndex);
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: "local-invalid");
        await fixture.AssertSourceUnchanged();
    }

    private static void AssertNoPublicationAfterFault(Fixture fixture, int failureIndex)
    {
        Assert.True(failureIndex > 0);
        Assert.DoesNotContain(fixture.GitHub.Requests.Skip(failureIndex), request => request.Method == "POST" && request.Path == "/graphql");
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Equal(0, fixture.Checkpoint().State.LineageCharges.PatchValidationInvocations);
    }

    private static async Task<Fixture> FiveTargetFixture(string scenario)
    {
        var fixture = await Fixture.CreateAsync(targetCount: 12, providerScenario: scenario);
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["budgets"]!["invocation"] = new JsonObject { ["maximumTargets"] = 5, ["maximumProviderRequests"] = 8 };
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());
        return fixture;
    }
}
