using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Killing_the_process_after_remote_PR_creation_recovers_that_PR_without_another_create_attempt()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GitHub.After = request =>
        {
            if (request.Method == "POST" && request.Kind == "pulls")
            {
                fixture.GitHub.DelayResponses = true;
                created.TrySetResult();
            }
        };
        using (var interrupted = CampaignCliProcessTests.Start(fixture.Args("start"), fixture.Environment()))
        {
            try { await created.Task.WaitAsync(TimeSpan.FromMinutes(3)); }
            finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        }
        fixture.GitHub.After = null;
        fixture.GitHub.DelayResponses = false;
        Assert.Single(fixture.GitHub.PullRequests);
        AssertResult(await fixture.Run("resume"), 0, "published");
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Killed_accepted_only_or_mixed_append_reservation_uses_fresh_retry_and_preserves_accounting(bool append)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        if (append) await ConfigureAppend(fixture);
        var acknowledgement = Path.Join(fixture.Outside, "patch-reserved.ack");
        var release = Path.Join(fixture.Outside, "never-released");
        var environment = fixture.Environment();
        foreach (var pair in CampaignCliProcessTests.HookEnvironment(
            ContractScribe.Cli.CampaignProcessBoundaryHooks.PatchAfterReservationReadback, acknowledgement, release))
            environment[pair.Key] = pair.Value;
        using (var interrupted = CampaignCliProcessTests.Start(fixture.Args("resume"), environment))
        {
            try
            {
                await CampaignCliProcessTests.WaitForFileAsync(acknowledgement, interrupted,
                    ContractScribe.Cli.CampaignProcessBoundaryHooks.PatchAfterReservationReadback, TimeSpan.FromMinutes(2));
                Assert.IsType<CampaignPatchReservation>(fixture.Checkpoint().State.ActiveReservation);
            }
            finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        }
        var reserved = fixture.Checkpoint();
        Assert.Equal(accepted.Sha256, reserved.State.AcceptedCandidateOrigin!.CheckpointSha256);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, append ? "published" : "replayed");
        var retried = fixture.Checkpoint();
        Assert.Null(retried.State.ActiveReservation);
        Assert.True(retried.State.LineageCharges.PatchValidationInvocations > reserved.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(append ? 2 : 1, retried.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        if (!append) Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Single(fixture.GitHub.PullRequests);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes(bool after)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var originalCheckpoint = await File.ReadAllBytesAsync(fixture.State);
        var authority = fixture.GitHub.Coordination()["authorityCommitmentSha256"]!.GetValue<string>();
        var operation = fixture.GitHub.Coordination()["operationCommitmentSha256"]!.GetValue<string>();
        var mutations = fixture.GitHub.Mutations;
        var normalCas = fixture.GitHub.SuccessfulCas;
        Assert.InRange(mutations, 20, 60);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index <= mutations; index++)
        {
            fixture.GitHub.Reset();
            fixture.State = Path.Join(fixture.Outside, $"loss-{after}-{index}.json");
            await WriteCheckpointCopy(fixture.State, originalCheckpoint);
            fixture.GitHub.LoseAt = index;
            fixture.GitHub.LoseAfter = after;
            var failed = await fixture.Run("resume");
            Assert.Contains(failed.ExitCode, new[] { 0, 3, 5 });
            var boundary = Assert.Single(fixture.GitHub.Requests, r => r.Mutation == index);
            covered.Add(boundary.Kind);
            fixture.GitHub.LoseAt = null;
            var beforeRestart = fixture.GitHub.Mutations;
            var restarted = await fixture.Run("resume");
            Assert.Contains(restarted.ExitCode, new[] { 0, 3 });
            var writes = fixture.GitHub.Mutations;
            // A cold invocation that made no writes already proves stable reconciliation.
            // If recovery advanced remote state, verify that result with another cold process.
            var stable = writes == beforeRestart ? restarted : await fixture.Run("resume");
            Assert.Equal(writes, fixture.GitHub.Mutations);
            Assert.Contains(stable.ExitCode, new[] { 0, 3 });
            Assert.InRange(fixture.GitHub.PrAttempts, 0, 1);
            Assert.InRange(fixture.GitHub.PullRequests.Count, 0, 1);
            Assert.InRange(fixture.GitHub.SuccessfulCas, 0, normalCas);
            Assert.All(fixture.GitHub.Requests, r => Assert.True(r.Method == "GET" || r.Method == "POST"));
            if (fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal)))
            {
                Assert.Equal(authority, fixture.GitHub.Coordination()["authorityCommitmentSha256"]!.GetValue<string>());
                Assert.Equal(operation, fixture.GitHub.Coordination()["operationCommitmentSha256"]!.GetValue<string>());
            }
            if (stable.ExitCode == 0)
            {
                AssertResult(stable, 0, "replayed");
                Assert.Single(fixture.GitHub.PullRequests);
                Assert.Equal("published", fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
            }
            await fixture.AssertSourceUnchanged();
        }
        Assert.Equal(new[] { "blobs", "cas", "commits", "pulls", "trees" }, covered.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_snapshot_append_accepts_new_work_and_its_fresh_process_retry_is_zero_write(bool newPath)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true, newPath: newPath);
        AssertResult(await fixture.Run("start"), 0, "published");
        var first = fixture.Checkpoint();
        Assert.Single(first.State.WorkItems, w => w.Status == CampaignWorkStatus.Accepted);
        Assert.Single(first.State.WorkItems, w => w.Status == CampaignWorkStatus.Planned);
        await ConfigureAppend(fixture);
        AssertResult(await fixture.Run("resume"), 0, "published");
        var second = fixture.Checkpoint();
        Assert.Equal(2, second.State.WorkItems.Count(w => w.Status == CampaignWorkStatus.Accepted));
        Assert.Equal(newPath ? 2 : 1, second.State.CandidateObservation!.ChangedFiles.Length);
        Assert.Equal(second.CheckpointRevision, second.State.AcceptedCandidateOrigin!.CheckpointRevision);
        Assert.Null(second.State.AcceptedCandidateOrigin.CheckpointSha256);
        Assert.NotEqual(first.State.CandidateObservation!.PatchResultCommitmentSha256,
            second.State.CandidateObservation!.PatchResultCommitmentSha256);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Independent_checkpoint_copies_reach_the_same_remote_race_without_duplicate_publication(bool differentOperation, bool append)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: append);
        if (append)
        {
            AssertResult(await fixture.Run("start"), 0, "published");
            await ConfigureAppend(fixture);
        }
        fixture.GitHub.RejectPath = "/repos/Owner/repo";
        AssertResult(await fixture.Run(append ? "resume" : "start"), 4, "permission");
        fixture.GitHub.RejectPath = null;
        if (!append) fixture.GitHub.Reset();
        var copy = Path.Join(fixture.Outside, "independent.json");
        await WriteCheckpointCopy(copy, await File.ReadAllBytesAsync(fixture.State));
        var otherArgs = fixture.Args("resume", copy);
        if (differentOperation)
        {
            var alternate = Path.Join(fixture.Outside, "other-github.json");
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.GitHubConfiguration))!;
            configuration["operationId"] = "operation.other";
            await File.WriteAllTextAsync(alternate, configuration.ToJsonString());
            otherArgs[^1] = alternate;
        }
        fixture.GitHub.Barrier("/git/ref/heads/contract-scribe/");
        using var first = CampaignCliProcessTests.Start(fixture.Args("resume"), fixture.Environment());
        var otherEnvironment = fixture.Environment();
        otherEnvironment["CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS"] = Path.Join(fixture.Outside, "other-observations.txt");
        using var second = CampaignCliProcessTests.Start(otherArgs, otherEnvironment);
        try
        {
            await Task.WhenAll(first.Process.WaitForExitAsync(), second.Process.WaitForExitAsync()).WaitAsync(TimeSpan.FromMinutes(3));
            var results = new[] { await first.CompleteAsync(), await second.CompleteAsync() };
            Assert.Contains(results, r => r.ExitCode == 0);
            Assert.All(results, r => Assert.Contains(r.ExitCode, new[] { 0, 3 }));
            Assert.Single(fixture.GitHub.PullRequests);
            Assert.Equal(1, fixture.GitHub.PrAttempts);
            Assert.True(fixture.GitHub.Requests.Count(r => r.Path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal)) >= 2);
            await fixture.AssertSourceUnchanged();
        }
        finally { await CampaignCliProcessTests.StopAsync(first); await CampaignCliProcessTests.StopAsync(second); }
    }

    private static async Task WriteCheckpointCopy(string path, byte[] bytes)
    {
        await File.WriteAllBytesAsync(path, bytes);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task ConfigureAppend(Fixture fixture)
    {
        var previous = fixture.GitHub.Coordination();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.GitHubConfiguration))!;
        config["operationId"] = "operation.append";
        config["transition"] = "same-snapshot-append";
        config["appendPredecessor"] = new JsonObject
        {
            ["operationId"] = previous["operationId"]!.DeepClone(),
            ["authorityCommitmentSha256"] = previous["authorityCommitmentSha256"]!.DeepClone(),
            ["candidateCommitmentSha256"] = previous["currentCandidateCommitmentSha256"]!.DeepClone(),
            ["generationId"] = previous["generationId"]!.DeepClone(),
            ["snapshotCommitmentSha256"] = previous["snapshotCommitmentSha256"]!.DeepClone(),
            ["policyCommitmentSha256"] = previous["policyCommitmentSha256"]!.DeepClone(),
            ["changedFiles"] = new JsonArray(previous["cumulativeChangedFiles"]!.AsArray().Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file!["path"]!.DeepClone(),
                ["candidateFileSha256"] = file["candidateSha256"]!.DeepClone(),
            }).ToArray()),
        };
        await File.WriteAllTextAsync(fixture.GitHubConfiguration, config.ToJsonString());
    }
}
