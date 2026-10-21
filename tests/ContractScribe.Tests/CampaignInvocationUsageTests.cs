using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Fact]
    public async Task A_generated_session_anchor_does_not_become_new_resumed_target_work()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var generated = await DocumentationCampaignProposalExecutor.ExecuteAsync(
            campaign.Input(fixture, store, new ProposalExchange(fixture.Request), RuntimeOptions()));
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.ProposalReady, generated.Kind);
        var saved = store.Current!;
        var anchor = saved.State.WorkItems.Single(work => work.Status == CampaignWorkStatus.ProposalComplete);
        var fresh = new InvocationUsage();
        fresh.ObserveState(saved);
        // A shared-session claim can retain its original, already generated anchor;
        // that journaling identity does not imply generation of this target again.
        fresh.ObserveExecution(saved, anchor.WorkItemKey);
        using var json = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(
            GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
                new("execution", CampaignOperation.Resume, "campaign.provider-retryable", saved.CheckpointRevision)), fresh).StandardOutput);
        var usage = json.RootElement.GetProperty("invocationUsage");
        Assert.Equal(0, usage.GetProperty("work").GetProperty("startedTargets").GetInt32());
        Assert.Equal(0, usage.GetProperty("work").GetProperty("resumedTargets").GetInt32());
        Assert.Equal(0, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        Assert.Equal(saved.State.LineageCharges, Assert.IsType<CampaignCheckpointArtifact>(store.Current).State.LineageCharges);
    }

    [Fact]
    public async Task Context_finish_is_observed_before_campaign_completion_discards_its_reason()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var usage = new InvocationUsage();
        var input = campaign.Input(fixture, store, new ContextPreparationStop(), RuntimeOptions()) with { Usage = usage };
        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.TerminalStop, outcome.Kind);
        Assert.Equal(DocumentationScribeInvocationStopReason.None, input.InvocationAllowance!.StopReason);
        Assert.Equal(0, input.InvocationAllowance.ProviderRequestCount);
        var result = GitHubProposalPresentation.WithUsage(GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Start, "campaign.provider-terminal", store.Current!.CheckpointRevision)), usage);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var terminal = json.RootElement.GetProperty("invocationUsage").GetProperty("termination");
        Assert.Equal("context-limit", terminal.GetProperty("stopReason").GetString());
        Assert.Equal("Context", terminal.GetProperty("scribeFinishReason").GetString());
        Assert.Equal("None", terminal.GetProperty("allowanceStopReason").GetString());
        Assert.Equal(5, result.ExitCode);
        Assert.Equal("github-proposal.host-failure", json.RootElement.GetProperty("outcome").GetString());
        // A later publication failure retains terminal precedence over the recorded Context.
        var failure = GitHubProposalPresentation.WithUsage(GitHubProposalPresentation.Local(
            CliBuildIdentity.Current, CampaignOperation.Start, "permission"), usage);
        using var failed = JsonDocument.Parse(failure.StandardOutput);
        Assert.Equal("failure", failed.RootElement.GetProperty("invocationUsage").GetProperty("termination").GetProperty("stopReason").GetString());
    }

    private sealed class ContextPreparationStop : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest request,
            CancellationToken token) => throw new InvalidOperationException("No physical send is allowed.");
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest request,
            DocumentationScribeExecutionScope scope, CancellationToken token) => ValueTask.FromResult(
                new DocumentationScribeModelResponse([], [], new(DocumentationScribeModelFailureCode.ContextLimit,
                    origin: DocumentationScribeModelFailureOrigin.RequestPreparation)));
    }
}
