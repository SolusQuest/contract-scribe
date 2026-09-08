using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Nonremovable_mixed_append_rejection_preserves_failure_without_publication()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync(twoWorks: true, newPath: true, rejectNewPath: true);
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        Assert.Equal("App/App.cs", Assert.Single(accepted.State.CandidateObservation!.ChangedFiles).Path);
        await ConfigureAppend(fixture);
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.Mutations;
        var rejected = await fixture.Run("resume");
        AssertResult(rejected, 5, "host-failure");
        Assert.Equal("github proposal stopped before publication: campaign.patch-rejected\n", rejected.Stderr);
        var settled = fixture.Checkpoint();
        Assert.Equal(CampaignTerminalKind.Failed, settled.State.TerminalOutcome!.Kind);
        Assert.Contains(settled.State.WorkItems, item => item.Status == CampaignWorkStatus.ProposalComplete);
        Assert.Equal(accepted.Sha256, settled.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Equal(accepted.State.CandidateObservation.PatchResultCommitmentSha256,
            settled.State.AcceptedCandidateOrigin.CandidateObservation.PatchResultCommitmentSha256);
        Assert.Equal(accepted.State.LineageCharges.PatchValidationInvocations + 1,
            settled.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(reads, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.Mutations);
        AssertResult(await fixture.Run("resume"), 5, "host-failure");
        Assert.Equal(settled.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(reads, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData("host", 5, "host-failure", "campaign.patch-host-failure")]
    [InlineData("rejected", 5, "host-failure", "campaign.patch-rejected")]
    [InlineData("stale", 4, "stale", "campaign.patch-stale")]
    public async Task Persisted_failed_reconstruction_wins_over_append_no_work(
        string failure, int exit, string outcome, string diagnostic)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        var failed = (await RealReconstruction(fixture, failure)).Artifact!;
        Assert.Equal(CampaignTerminalKind.Failed, failed.State.TerminalOutcome!.Kind);
        Assert.Null(failed.State.ActiveReservation);
        Assert.Equal(accepted.Sha256, failed.State.AcceptedCandidateOrigin!.CheckpointSha256);
        await ConfigureAppend(fixture);
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.Mutations;
        var result = await fixture.Run("resume");
        AssertResult(result, exit, outcome);
        Assert.Equal("github proposal stopped before publication: " + diagnostic + "\n", result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(failed.CheckpointRevision, json.RootElement.GetProperty("checkpointRevision").GetInt64());
        Assert.Equal(failed.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(reads, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Invalid_escaped_configuration_is_local_invalid_before_campaign_or_token_access()
    {
        await using var fixture = await Fixture.CreateAsync();
        var raw = await File.ReadAllTextAsync(fixture.GitHubConfiguration);
        await File.WriteAllTextAsync(fixture.GitHubConfiguration,
            raw.Replace("operation.initial", "\\uD800", StringComparison.Ordinal));
        var result = await fixture.Run("start");
        AssertResult(result, 4, "local-invalid");
        Assert.Equal("github proposal stopped before publication: github-proposal.local-invalid\n", result.Stderr);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public async Task Genuine_predecessor_reconstruction_at_handoff_is_not_new_append_work()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var accepted = fixture.Checkpoint();
        var reconstruction = await RealReconstruction(fixture, "accepted");
        await ConfigureAppend(fixture);
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var preflight = CampaignPreflight.Run(arguments.Campaign, fixture.Repository.Root);
        var github = GitHubProposalConfigurationReader.Read(arguments.GitHubConfiguration, fixture.Repository.Root);
        var continuation = new CampaignAcceptedCandidateContinuation(CliBuildIdentity.Current, preflight, github,
            _ => throw new InvalidOperationException("No credential is needed to classify append progress."));
        var current = fixture.Checkpoint();
        Assert.Equal(accepted.Sha256, current.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Null(continuation.ValidateHandoff(current, reconstruction));
        Assert.False(continuation.ReconstructAccepted(current.State));
        Assert.True(continuation.HasNoAppendWork(current.State));
        var reads = fixture.TokenReads();
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), 0, "no-op");
        Assert.Equal(current.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(reads, fixture.TokenReads());
        Assert.Equal(writes, fixture.GitHub.Mutations);
        await fixture.AssertSourceUnchanged();
    }

    private static async Task<DocumentationCampaignOutcome> RealReconstruction(Fixture fixture, string failure)
    {
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args("resume").AsSpan(1)).Arguments!;
        var preflight = CampaignPreflight.Run(arguments.Campaign, fixture.Repository.Root);
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
                    result = await DocumentationCampaignPatchExecutor.ExecuteAsync(new(bundle.Classified, bundle.Observed,
                        bundle.Policy, bundle.AuditInputs.ToImmutableArray(), bundle.Audit, planning, plan, execution,
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                        JsonSerializer.SerializeToElement(new
                        {
                            m2ProjectionVersion = 1,
                            maximumPatchElapsedMilliseconds = configuration.Planning.MaximumPatchElapsedMilliseconds,
                        }),
                        new FileCampaignCheckpointStore(fixture.State, fixture.Repository.Root), token, token,
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
