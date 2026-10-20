using System.Collections.Immutable;
using System.Reflection;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controlled_partial_stop_hands_off_only_the_independently_validated_candidate(bool patchTimeout)
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        if (patchTimeout)
            layer["planning"] = new JsonObject { ["maximumPatchElapsedMilliseconds"] = 1 };
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());
        var result = await fixture.Run("start");
        var checkpoint = fixture.Checkpoint();
        Assert.Equal(2, checkpoint.State.Batch.SelectedTargetKeys.Length);
        Assert.True(checkpoint.State.CumulativeOutcome is not null, result.Stdout + result.Stderr);
        Assert.Null(checkpoint.State.ActiveReservation);
        if (patchTimeout)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(0, fixture.GitHub.PublicationWrites);
            Assert.Equal(CampaignCumulativeOutcomeKind.Timeout, checkpoint.State.CumulativeOutcome!.Kind);
            Assert.Null(checkpoint.State.CandidateObservation);
        }
        else
        {
            AssertResult(result, 0, "published");
            Assert.Equal(CampaignCumulativeOutcomeKind.Accepted, checkpoint.State.CumulativeOutcome!.Kind);
            Assert.Single(checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys);
            Assert.Single(checkpoint.State.WorkItems.Where(work => work.Status == CampaignWorkStatus.Accepted));
            var writes = fixture.GitHub.PublicationWrites;
            var sends = fixture.Provider.RequestCount;
            AssertResult(await fixture.Run("resume"), 0, "awaiting-review");
            Assert.Equal(writes, fixture.GitHub.PublicationWrites);
            Assert.Equal(sends, fixture.Provider.RequestCount);
            Assert.Single(fixture.Checkpoint().State.WorkItems.Where(work => work.Status == CampaignWorkStatus.Accepted));
        }
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Published_gate_prevents_an_unresolved_mixed_result_set_from_reaching_patch_rejection()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true, newPath: true, rejectNewPath: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        await ConfigureSealedResume(fixture);
        var before = GateCounts(fixture);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, "awaiting-review");
        Assert.Equal(accepted.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(before, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData("host", 5, "host-failure", "campaign.patch-host-failure")]
    [InlineData("rejected", 5, "host-failure", "campaign.patch-rejected")]
    [InlineData("stale", 4, "stale", "campaign.patch-stale")]
    public async Task Persisted_failed_reconstruction_wins_over_sealed_batch_wait(
        string failure, int exit, string outcome, string diagnostic)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        var failed = (await RealReconstruction(fixture, failure)).Artifact!;
        Assert.Equal(CampaignTerminalKind.Failed, failed.State.TerminalOutcome!.Kind);
        Assert.Null(failed.State.ActiveReservation);
        Assert.Equal(accepted.Sha256, failed.State.AcceptedCandidateOrigin!.CheckpointSha256);
        await ConfigureSealedResume(fixture);
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.PublicationWrites;
        var result = await fixture.Run("resume");
        Assert.Equal(exit, outcome == "stale" ? 4 : 5);
        Assert.StartsWith("campaign.patch-", diagnostic, StringComparison.Ordinal);
        AssertResult(result, 0, "awaiting-review");
        Assert.Empty(result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(failed.CheckpointRevision, json.RootElement.GetProperty("checkpointRevision").GetInt64());
        Assert.Equal(failed.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(reads + 1, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Invalid_escaped_request_is_local_invalid_before_campaign_or_token_access()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.Args("start");
        var raw = await File.ReadAllTextAsync(fixture.Request);
        await File.WriteAllTextAsync(fixture.Request,
            raw.Replace("operation.initial", "\\uD800", StringComparison.Ordinal));
        var result = await fixture.RunArgs(arguments, fixture.Environment());
        AssertResult(result, 4, "local-invalid");
        Assert.Equal("github proposal stopped before publication: github-proposal.local-invalid\n", result.Stderr);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public async Task Genuine_original_reconstruction_preserves_frozen_origin_and_is_not_new_work()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        var reconstruction = await RealReconstruction(fixture, "accepted");
        await ConfigureSealedResume(fixture);
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var request = GitHubProposalRequestReader.Read(arguments.Request, fixture.Repository.Root);
        var preflight = CampaignPreflight.Run(arguments.Campaign(request), fixture.Repository.Root, CliBuildIdentity.Current, remoteCheckpoint: true)
            with
        { SnapshotBinding = fixture.Checkpoint().State.Snapshot.OpaqueSnapshotBinding };
        var continuation = new CampaignAcceptedCandidateContinuation(CliBuildIdentity.Current, preflight, request,
            _ => throw new InvalidOperationException("No credential is needed to classify original progress."));
        var current = fixture.Checkpoint();
        Assert.Equal(accepted.Sha256, current.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Null(continuation.ValidateHandoff(current, reconstruction));
        Assert.True(continuation.ReconstructAccepted(current.State));
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run("resume"), 0, "awaiting-review");
        Assert.Equal(current.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(reads + 1, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        await fixture.AssertSourceUnchanged();
    }

    private static async Task<DocumentationCampaignOutcome> RealReconstruction(Fixture fixture, string failure)
    {
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var request = GitHubProposalRequestReader.Read(arguments.Request, fixture.Repository.Root);
        var preflight = CampaignPreflight.Run(arguments.Campaign(request), fixture.Repository.Root, CliBuildIdentity.Current, remoteCheckpoint: true)
            with
        { SnapshotBinding = fixture.Checkpoint().State.Snapshot.OpaqueSnapshotBinding };
        var github = request.Request.GitHub;
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [fixture.GitHub.Endpoint, null, 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            github.RepositoryOwner, github.RepositoryName, github.TargetRef, request.Request.CampaignLineage,
            github.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        await using var checkpoint = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await checkpoint.AcquireCheckpointAsync(default));
        var configuration = preflight.Configuration.Document;
        var sourcePath = Path.Join(fixture.Repository.Root, "App", "App.cs");
        string? staging = null;
        var engine = failure == "host" ? new DocumentationPatchEngine(() => sourcePath, null, null)
            : new DocumentationPatchEngine(null, (stage, root) =>
            {
                if (stage == DocumentationPatchApplicationStage.AfterSealBeforeReturn) staging = root;
                if (failure == "stale" && stage == DocumentationPatchApplicationStage.BaselineCaptured)
                    File.AppendAllText(sourcePath, "// synthetic movement during reconstruction\n");
            }, stage =>
            {
                if (failure == "rejected" && stage == DocumentationPatchEngineStage.BeforeCandidateTerminalPass)
                    File.AppendAllText(Path.Join(staging!, "App", "App.cs"), " ");
            });
        DocumentationCampaignOutcome? result = null;
        var host = new ProductionRepositorySessionHost(new HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
        try
        {
            await host.RunAsync(new ProductionAuditRequest(fixture.Repository.Root, preflight.InputPath,
                preflight.PolicyBytes, PublicationTarget: null, PublishResult: false),
                new ProductionAuditHostControls(SessionConsumer: async (bundle, token) =>
                {
                    var policy = configuration.CreateExecutionPolicy();
                    var execution = configuration.CreateExecutionCapability(policy);
                    var planning = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token);
                    var plan = CampaignPlanner.Plan(planning);
                    Assert.True(await checkpoint.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(plan), token));
                    result = await DocumentationCampaignPatchExecutor.ExecuteAsync(new(bundle.Classified, bundle.Observed,
                        bundle.Policy, bundle.AuditInputs.ToImmutableArray(), bundle.Audit, planning, plan, execution,
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                        JsonSerializer.SerializeToElement(new
                        {
                            m2ProjectionVersion = 1,
                            maximumPatchElapsedMilliseconds = configuration.Planning.MaximumPatchElapsedMilliseconds,
                        }),
                        checkpoint, token, token,
                        PatchEngine: engine, AcceptedOnly: true));
                }), CancellationToken.None);
        }
        finally { await File.WriteAllBytesAsync(sourcePath, fixture.Source); }
        Assert.NotNull(result?.Artifact);
        Assert.Equal(failure switch
        {
            "host" => DocumentationCampaignOutcomeKind.HostFailure,
            "rejected" => DocumentationCampaignOutcomeKind.Rejected,
            "stale" => DocumentationCampaignOutcomeKind.Stale,
            _ => DocumentationCampaignOutcomeKind.Reconstructed,
        }, result.Kind);
        return result;
    }
}
