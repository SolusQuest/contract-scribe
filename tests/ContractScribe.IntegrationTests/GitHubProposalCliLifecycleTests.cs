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
    public async Task Closed_unmerged_successor_requires_exact_explicit_authorization_and_replays_it_across_processes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settlementPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GitHub.BeforeBody = (request, bytes) =>
        {
            if (request.Method != "POST" || request.Kind != "blobs") return;
            using var body = JsonDocument.Parse(bytes);
            var candidate = CampaignStateJson.Parse(Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!));
            if (candidate.Artifact?.State.DispatchObservations.Any(row => row.State == CampaignDispatchObservationState.Settled) != true) return;
            fixture.GitHub.DelayResponses = true;
            settlementPrepared.TrySetResult();
        };
        using (var interrupted = CampaignCliProcessTests.Start(fixture.Args("start"), fixture.Environment()))
        {
            try
            {
                await settlementPrepared.Task.WaitAsync(TimeSpan.FromMinutes(2));
                Assert.True(fixture.Provider.RequestCount > 0);
                Assert.Equal(CampaignDispatchObservationState.Reserved, Assert.Single(fixture.Checkpoint().State.DispatchObservations).State);
            }
            finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        }
        fixture.GitHub.BeforeBody = null;
        fixture.GitHub.DelayResponses = false;
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        AssertResult(await fixture.Run("resume"), 0, "published");
        var beforeArchive = fixture.Checkpoint();
        Assert.Contains(beforeArchive.State.DispatchObservations, row => row.State == CampaignDispatchObservationState.Unknown);
        Assert.Contains(beforeArchive.State.DispatchObservations, row => row.State == CampaignDispatchObservationState.Settled);
        await ArchiveRemoteCheckpoint(fixture);
        var archived = fixture.Checkpoint();
        Assert.Empty(archived.State.DispatchObservations);
        Assert.Equal(beforeArchive.State.LineageCharges, archived.State.LineageCharges);
        var archivePointer = fixture.GitHub.CoordinationDocument()["archive"]!.ToJsonString();
        fixture.GitHub.Lifecycle("closed");
        AssertResult(await fixture.Run("resume"), 4, "closed-unmerged");
        var closed = fixture.GitHub.PullRequests[0];
        fixture.Snapshot = "snapshot.github.successor";
        await fixture.AssertSourceUnchanged();
        var sourcePath = Path.Join(fixture.Repository.Root, "App", "App.cs");
        var successorSource = System.Text.Encoding.UTF8.GetBytes("// Explicit successor snapshot.\n" +
            await File.ReadAllTextAsync(sourcePath));
        await File.WriteAllBytesAsync(sourcePath, successorSource);
        fixture.GovernedFiles["App/App.cs"] = (successorSource, fixture.GovernedFiles["App/App.cs"].Mode);
        lock (fixture.GitHub.Gate)
        {
            var basis = fixture.GitHub.Commits[fixture.GitHub.BaseOid];
            var successorBase = fixture.GitHub.AddCommit(new JsonObject
            {
                ["tree"] = fixture.GitHub.BuildTree(fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes)),
                ["parents"] = new JsonArray(fixture.GitHub.BaseOid),
                ["message"] = "Explicit changed-source successor base\n",
                ["author"] = basis["author"]!.DeepClone(),
                ["committer"] = basis["committer"]!.DeepClone(),
            });
            fixture.GitHub.Refs["refs/heads/main"] = successorBase;
            fixture.Publication["expectedBaseCommitOid"] = successorBase;
        }
        fixture.Publication["operationId"] = "operation.successor";
        fixture.Publication["generationId"] = "generation.successor";
        fixture.Publication["transition"] = "successor-after-closed-unmerged";
        fixture.Publication["terminalPredecessor"] = new JsonObject
        {
            ["logicalPredecessorId"] = "predecessor.closed",
            ["pullRequestNumber"] = 1,
            ["generationId"] = "generation.initial",
            ["headOid"] = closed["head"]!["sha"]!.DeepClone(),
            ["disposition"] = "closed-unmerged",
        };
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.PublicationWrites;
        // A real fresh M4 candidate can be accepted locally, but absence of the explicit
        // authorization still stops H1 before any publication mutation. The checkpoint claim requires credentials.
        AssertResult(await fixture.Run("resume"), 4, "local-invalid");
        Assert.Equal(reads + 1, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        var fresh = fixture.Checkpoint();
        fixture.Publication["closedUnmergedSuccessorAuthorization"] = new JsonObject
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
        AssertResult(await fixture.Run("resume"), 0, "published");
        var successorCheckpoint = fixture.Checkpoint();
        Assert.NotEqual(archived.State.Snapshot.ExecutionCommitmentSha256, successorCheckpoint.State.Snapshot.ExecutionCommitmentSha256);
        Assert.Equal(archived.State.ArchivedDispatchPrefix, successorCheckpoint.State.ArchivedDispatchPrefix);
        Assert.Equal(archivePointer, fixture.GitHub.CoordinationDocument()["archive"]!.ToJsonString());
        Assert.All(successorCheckpoint.State.DispatchObservations, row =>
            Assert.Equal(successorCheckpoint.State.Snapshot.ExecutionCommitmentSha256, row.ExecutionCommitmentSha256));
        Assert.True(successorCheckpoint.State.LineageCharges.ProviderRequests.TotalCharged > archived.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.NotEqual(archived.State.AcceptedCandidateOrigin!.CheckpointSha256,
            successorCheckpoint.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.False(File.Exists(fixture.State));
        Assert.Equal(2, fixture.GitHub.PullRequests.Count);
        Assert.Single(fixture.GitHub.PullRequests, pr => pr["state"]!.GetValue<string>() == "open");
        writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run("resume"), 0, "replayed");
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Policy_change_holds_the_verified_predecessor_with_its_generation_and_no_attempted_operation_id()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        await ConfigureAppend(fixture);
        fixture.Publication["policy"]!["maximumDocumentationBlocks"] = 127;
        fixture.Publication["appendPredecessor"]!["policyCommitmentSha256"] = GitHubPublicationCommitments.CreatePolicy(
            new(128, 128, 4194304), new(127, 128, 4194304));
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
        AssertResult(restored, 3, "conflict");
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertResult(await fixture.Run("resume"), 3, "conflict");
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
