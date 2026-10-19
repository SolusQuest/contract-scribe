using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(ContractScribe.Cli.CampaignProcessBoundaryHooks.PhysicalReservationAfterReadback, false)]
    [InlineData(ContractScribe.Cli.CampaignProcessBoundaryHooks.PhysicalSettlementAfterReadback, true)]
    public async Task Remote_provider_crash_preserves_request_identity_and_known_or_unknown_exposure(string boundary, bool settled)
    {
        await using var fixture = await Fixture.CreateAsync();
        var acknowledgement = Path.Join(fixture.Outside, "provider-boundary.ack");
        var environment = fixture.Environment();
        foreach (var pair in CampaignCliProcessTests.HookEnvironment(boundary, acknowledgement,
            Path.Join(fixture.Outside, "never-released"))) environment[pair.Key] = pair.Value;
        CampaignCheckpointArtifact interruptedState;
        using (var interrupted = CampaignCliProcessTests.Start(fixture.Args("start"), environment))
        {
            try
            {
                await CampaignCliProcessTests.WaitForFileAsync(acknowledgement, interrupted, boundary, TimeSpan.FromMinutes(2));
                interruptedState = fixture.Checkpoint();
                Assert.Equal(settled ? CampaignDispatchObservationState.Settled : CampaignDispatchObservationState.Reserved,
                    Assert.Single(interruptedState.State.DispatchObservations).State);
            }
            finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        }
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        AssertResult(await fixture.Run("resume"), 0, "published");
        var restored = fixture.Checkpoint();
        var prior = Assert.Single(interruptedState.State.DispatchObservations);
        var retained = Assert.Single(restored.State.DispatchObservations,
            row => row.AttemptId == prior.AttemptId && row.DispatchOrdinal == prior.DispatchOrdinal);
        Assert.Equal(prior.RequestCommitmentSha256, retained.RequestCommitmentSha256);
        Assert.Equal(settled ? CampaignDispatchObservationState.Settled : CampaignDispatchObservationState.Unknown, retained.State);
        if (settled) Assert.Equal(prior.Charges, retained.Charges);
        else Assert.True(retained.Charges!.ProviderRequests.ConservativeUnobserved > 0);
        Assert.True(restored.State.LineageCharges.ProviderRequests.TotalCharged >= interruptedState.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.False(File.Exists(fixture.State));
        Assert.Single(fixture.GitHub.PullRequests);
        await fixture.AssertSourceUnchanged();
    }

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
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        Assert.Single(fixture.GitHub.PullRequests);
        AssertResult(await fixture.Run("resume"), 0, "published");
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Killed_accepted_only_or_mixed_append_reservation_uses_fresh_retry_and_preserves_accounting(bool append)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
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
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        var reserved = fixture.Checkpoint();
        Assert.Equal(accepted.Sha256, reserved.State.AcceptedCandidateOrigin!.CheckpointSha256);
        var writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run("resume"), 0, append ? "published" : "replayed");
        var retried = fixture.Checkpoint();
        Assert.Null(retried.State.ActiveReservation);
        Assert.True(retried.State.LineageCharges.PatchValidationInvocations > reserved.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(append ? 2 : 1, retried.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        if (!append) Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        Assert.Single(fixture.GitHub.PullRequests);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_snapshot_append_accepts_new_work_and_its_fresh_process_retry_is_zero_write(bool newPath)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
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
        var writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Independent_processes_compete_for_the_same_ref_claim_without_duplicate_publication(bool differentOperation, bool append)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: append);
        if (append)
        {
            AssertResult(await fixture.Run("start"), 0, "published");
            await ConfigureAppend(fixture);
        }
        var otherRequest = Path.Join(fixture.Outside, "other-request.json");
        var otherArgs = fixture.Args(append ? "resume" : "start", requestPath: otherRequest);
        if (differentOperation)
        {
            var request = JsonNode.Parse(await File.ReadAllTextAsync(otherRequest))!;
            request["github"]!["operationId"] = "operation.other";
            await File.WriteAllTextAsync(otherRequest, request.ToJsonString());
        }
        fixture.GitHub.Barrier("/git/ref/heads/contract-scribe/");
        using var first = CampaignCliProcessTests.Start(fixture.Args(append ? "resume" : "start"), fixture.Environment());
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

    private static async Task ConfigureAppend(Fixture fixture)
    {
        var previous = fixture.GitHub.Coordination();
        fixture.Publication["operationId"] = "operation.append";
        fixture.Publication["transition"] = "same-snapshot-append";
        fixture.Publication["appendPredecessor"] = new JsonObject
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
        var path = Assert.IsType<string>(fixture.Configuration);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        configuration["budgets"]!["invocation"]!["maximumProviderRequests"] = 64;
        await File.WriteAllTextAsync(path, configuration.ToJsonString());
    }
}
