using System.Reflection;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Empty_fixed_batch_with_deferred_targets_is_no_op_without_publication_or_provider_dispatch()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(unsupportedProperty: true);
        await CreateFixedBatchCheckpoint(fixture, 0);
        var initial = fixture.Checkpoint();
        Assert.Empty(initial.State.Batch.SelectedTargetKeys);
        Assert.Equal(2, initial.State.Batch.CompleteTargets.Length);
        Assert.All(initial.State.TargetProgress, item => Assert.Equal(CampaignTargetProgressKind.Deferred, item.Kind));
        Assert.Equal(CampaignTerminalReason.AllWorkClosed, initial.State.TerminalOutcome!.Reason);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var stopped = await fixture.Run("resume");
            AssertResult(stopped, 0, "no-op");
            Assert.Equal("", stopped.Stderr);
            Assert.Equal(initial.Sha256, fixture.Checkpoint().Sha256);
            Assert.Equal(0, fixture.Checkpoint().State.LineageCharges.OuterInvocations);
            Assert.Equal(0, fixture.Checkpoint().State.LineageCharges.PatchValidationInvocations);
            Assert.Equal(repeat + 1, fixture.TokenReads());
            Assert.Equal(0, fixture.Provider.RequestCount);
            Assert.Equal(0, fixture.GitHub.PublicationWrites);
        }
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData(false, 0, "no-op")]
    [InlineData(true, 5, "host-failure")]
    public async Task Same_predecessor_append_stops_a_fixed_batch_without_reconstruction_or_publication(
        bool unresolved, int exit, string outcome)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: !unresolved, newPath: !unresolved,
            unsupportedProperty: unresolved);
        await CreateFixedBatchCheckpoint(fixture, unresolved ? 100 : 1);
        var originalBatch = fixture.Checkpoint().State.Batch;
        Assert.Equal(2, originalBatch.CompleteTargets.Length);
        AssertResult(await fixture.Run("resume"), 0, "published");
        var accepted = fixture.Checkpoint();
        Assert.Equal(originalBatch.Identity, accepted.State.Batch.Identity);
        Assert.Single(accepted.State.WorkItems, item => item.Status == CampaignWorkStatus.Accepted);
        Assert.Equal(unresolved ? CampaignTerminalReason.Unresolved : CampaignTerminalReason.AllWorkClosed,
            accepted.State.TerminalOutcome!.Reason);
        Assert.Contains(accepted.State.TargetProgress, item => item.Kind == (unresolved
            ? CampaignTargetProgressKind.UnsupportedCurrentExecutor : CampaignTargetProgressKind.Deferred));
        await ConfigureAppend(fixture);
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.PublicationWrites;
        var providerRequests = fixture.Provider.RequestCount;
        var gates = GateCounts(fixture);
        Assert.Equal(unresolved ? 5 : 0, exit);
        Assert.Equal(unresolved ? "host-failure" : "no-op", outcome);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var stopped = await fixture.Run("resume");
            AssertResult(stopped, 0, "awaiting-review");
            Assert.Empty(stopped.Stderr);
            var current = fixture.Checkpoint();
            Assert.Equal(accepted.Sha256, current.Sha256);
            Assert.Equal(accepted.State.LineageCharges.PatchValidationInvocations, current.State.LineageCharges.PatchValidationInvocations);
            Assert.Equal(reads + repeat + 1, fixture.TokenReads());
            Assert.Equal(writes, fixture.GitHub.PublicationWrites);
            Assert.Equal(providerRequests, fixture.Provider.RequestCount);
            Assert.Equal(gates, GateCounts(fixture));
        }
        await fixture.AssertSourceUnchanged();
    }

    private static async Task CreateFixedBatchCheckpoint(Fixture fixture, int limit)
    {
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var request = GitHubProposalRequestReader.Read(arguments.Request, fixture.Repository.Root);
        var preflight = CampaignPreflight.Run(arguments.Campaign(request), fixture.Repository.Root, CliBuildIdentity.Current, remoteCheckpoint: true);
        var github = request.Request.GitHub;
        using var registration = (IDisposable)typeof(GitHubTransportTestHook)
            .GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [fixture.GitHub.Endpoint, null, 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            github.RepositoryOwner, github.RepositoryName, github.TargetRef, request.Request.CampaignLineage,
            github.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        await using var checkpoint = GitHubCoordinationStore.Create(client);
        var observed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, checkpoint, default);
        preflight = preflight with { SnapshotBinding = GitHubCampaignPreflight.SnapshotBinding(preflight, request.Request, observed) };
        Assert.Equal(CampaignCheckpointReadKind.Found, await checkpoint.AcquireCheckpointAsync(default));
        var configuration = preflight.Configuration.Document;
        CampaignCheckpointArtifact? initial = null;
        var host = new ProductionRepositorySessionHost(new HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
        await host.RunAsync(new(fixture.Repository.Root, preflight.InputPath, preflight.PolicyBytes,
            PublicationTarget: null, PublishResult: false), new ProductionAuditHostControls(SessionConsumer: async (bundle, token) =>
            {
                var policy = configuration.CreateExecutionPolicy();
                var execution = configuration.CreateExecutionCapability(policy);
                var planning = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token, new(limit));
                var plan = CampaignPlanner.Plan(planning);
                initial = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
                    configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                    configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, execution,
                    bundle.Session.InputIdentity, planning, plan));
                Assert.True(await checkpoint.BindPlanAsync(CampaignPlanner.CreateRecoveryProjection(plan), token));
                Assert.Equal(CampaignCheckpointWriteKind.Written, (await checkpoint.CreateIfAbsentAsync(
                    initial.ExactUtf8Json.AsMemory(), initial.CheckpointRevision, initial.Sha256, token)).Kind);
            }), CancellationToken.None);
        Assert.NotNull(initial);
        Assert.False(File.Exists(fixture.State));
    }
}
