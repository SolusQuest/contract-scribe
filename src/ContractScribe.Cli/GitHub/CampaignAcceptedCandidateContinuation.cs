using ContractScribe.Core;
using ContractScribe.GitHub.Publication;

namespace ContractScribe.Cli;

internal sealed record CampaignRunSelection(CampaignTerminal? Terminal, CliExecutionResult? Result)
{
    public static implicit operator CampaignRunSelection(CampaignTerminal terminal) => new(terminal, null);
}

internal sealed class CampaignAcceptedCandidateContinuation(
    CliBuildIdentity identity, CampaignPreflightResult preflight,
    GitHubProposalConfigurationSnapshot githubConfiguration, Func<string, string?> credentialAccessor)
{
    internal CliExecutionResult Present(CampaignTerminal terminal) => GitHubProposalPresentation.Campaign(identity, terminal);

    internal bool ReconstructAccepted(CampaignCheckpointState state)
    {
        if (state.CandidateObservation is null) return false;
        var configuration = githubConfiguration.Configuration;
        return configuration.Transition != GitHubPublicationTransitionKind.SameSnapshotAppend
            || configuration.AppendPredecessor?.CandidateCommitmentSha256
                != state.AcceptedCandidateOrigin?.CandidateObservation.PatchResultCommitmentSha256;
    }

    internal bool HasNoAppendWork(CampaignCheckpointState state) =>
        state.CandidateObservation is not null && !ReconstructAccepted(state)
        && state.ActiveReservation is null
        && state.WorkItems.All(item => item.Status == CampaignWorkStatus.Accepted
            || item.Status == CampaignWorkStatus.Closed && item.ClosedOutcome is not
            {
                Code: CampaignWorkOutcomeCode.ProviderFailure,
                ProviderDisposition: CampaignProviderFinalDisposition.Retryable,
            });

    internal async Task<CampaignRunSelection> ContinueAsync(
        GitHubPublicationContext context, DocumentationCampaignOutcome outcome)
    {
        var revision = context.CurrentCheckpoint.CheckpointRevision;
        if (outcome.Artifact is null || outcome.AcceptedCandidate is null
            || outcome.CheckpointFailure is not null
            || outcome.Artifact.State.CandidateObservation is null
            || outcome.Artifact.State.CumulativeOutcome?.Kind != CampaignCumulativeOutcomeKind.Accepted)
            return new(null, GitHubProposalPresentation.ContractError(identity, preflight.Operation, revision));
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!preflight.Configuration.Revalidate() || !githubConfiguration.Revalidate())
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            var admitted = GitHubPublicationRequestFactory.Create(context, outcome, githubConfiguration.Configuration);
            if (!admitted.IsValid)
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            context.CancellationToken.ThrowIfCancellationRequested();
            // Revalidate after candidate capture too; no ambient credential access occurs in H1.
            if (!preflight.Configuration.Revalidate() || !githubConfiguration.Revalidate())
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            var observed = await GitHubPublicationFacade.PublishAsync(admitted.Authority!, admitted.Payload!,
                () => credentialAccessor("CONTRACTSCRIBE_GITHUB_TOKEN"), context.CancellationToken).ConfigureAwait(false);
            return new(null, GitHubProposalPresentation.Publication(identity, preflight.Operation, revision, observed));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "cancelled", revision));
        }
    }
}
