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
        && state.CumulativeOutcome?.Kind == CampaignCumulativeOutcomeKind.Accepted
        && state.TerminalOutcome is null or { Kind: CampaignTerminalKind.Complete }
        && state.ActiveReservation is null
        && state.WorkItems.All(item => item.Status == CampaignWorkStatus.Accepted
            || item.Status == CampaignWorkStatus.Closed && item.ClosedOutcome is not
            {
                Code: CampaignWorkOutcomeCode.ProviderFailure,
                ProviderDisposition: CampaignProviderFinalDisposition.Retryable,
            });

    internal CampaignTerminal? PersistedStop(CampaignCheckpointState state)
    {
        if (state.TerminalOutcome is null) return null;
        var outcome = state.CumulativeOutcome?.Kind switch
        {
            CampaignCumulativeOutcomeKind.HostFailure => "campaign.patch-host-failure",
            CampaignCumulativeOutcomeKind.Rejected => "campaign.patch-rejected",
            CampaignCumulativeOutcomeKind.Stale => "campaign.patch-stale",
            _ when state.TerminalOutcome.Kind == CampaignTerminalKind.Failed => "campaign.host-contract-error",
            _ => null,
        };
        return outcome is null ? null : new("execution", preflight.Operation, outcome, state.CheckpointRevision);
    }

    internal CliExecutionResult? ValidateHandoff(CampaignCheckpointArtifact current, DocumentationCampaignOutcome outcome)
    {
        if (outcome.Kind is not (DocumentationCampaignOutcomeKind.Accepted or DocumentationCampaignOutcomeKind.Reconstructed)
            || outcome.Artifact is not { } artifact || outcome.AcceptedCandidate is null || outcome.CheckpointFailure is not null
            || artifact.CheckpointRevision != current.CheckpointRevision || artifact.Sha256 != current.Sha256
            || !artifact.ExactUtf8Json.AsSpan().SequenceEqual(current.ExactUtf8Json.AsSpan())
            || current.State.CandidateObservation is null
            || current.State.CumulativeOutcome?.Kind != CampaignCumulativeOutcomeKind.Accepted)
            return GitHubProposalPresentation.ContractError(identity, preflight.Operation, current.CheckpointRevision);
        if (!preflight.Configuration.Revalidate() || !githubConfiguration.Revalidate())
            return GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", current.CheckpointRevision);
        return null;
    }

    internal async Task<CampaignRunSelection> ContinueAsync(
        GitHubPublicationContext context, DocumentationCampaignOutcome outcome)
    {
        var revision = context.CurrentCheckpoint.CheckpointRevision;
        if (ValidateHandoff(context.CurrentCheckpoint, outcome) is { } failure) return new(null, failure);
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
