using ContractScribe.Core;
using ContractScribe.GitHub.Publication;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record CampaignRunSelection(CampaignTerminal? Terminal, CliExecutionResult? Result)
{
    public static implicit operator CampaignRunSelection(CampaignTerminal terminal) => new(terminal, null);
}

internal sealed class CampaignAcceptedCandidateContinuation(
    CliBuildIdentity identity, CampaignPreflightResult preflight,
    GitHubCampaignConfigurationSnapshot request,
    Func<CampaignCheckpointState, GitHubPublicationConfiguration>? selectPublication = null,
    Func<ValidatedGitHubPublicationAuthority, ValidatedGitHubChangedFilePayload, CancellationToken, Task<GitHubPublicationObservation>>? publish = null,
    Func<ProductionRepositorySessionBundle, CancellationToken, Task<CliExecutionResult?>>? beforePlanning = null,
    GitHubPublicationFinalization? finalization = null,
    Action<CancellationToken>? bindFinalizationDeadline = null,
    bool originalOperationOnly = false)
{
    internal bool OriginalOperationOnly => originalOperationOnly;
    internal CancellationToken BeginFinalization(CancellationToken token)
    {
        if (finalization is null) return token;
        var execution = finalization.Begin(token);
        bindFinalizationDeadline?.Invoke(finalization.DeadlineToken);
        return execution;
    }
    internal CliExecutionResult? FinalizationFailure(long? revision) => finalization?.TimedOut == true
        ? GitHubProposalPresentation.Local(identity, preflight.Operation, "timeout", revision) : null;
    internal bool RevalidateInputs() => request.Revalidate() && preflight.Configuration.Revalidate();
    internal Task<CliExecutionResult?> BeforePlanningAsync(ProductionRepositorySessionBundle bundle, CancellationToken token) =>
        beforePlanning?.Invoke(bundle, token) ?? Task.FromResult<CliExecutionResult?>(null);
    internal CliExecutionResult Present(CampaignTerminal terminal) => GitHubProposalPresentation.Campaign(identity, terminal);

    internal bool ReconstructAccepted(CampaignCheckpointState state) => state.CandidateObservation is not null;

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
        if (!preflight.Configuration.Revalidate() || !request.Revalidate())
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
            if (!preflight.Configuration.Revalidate() || !request.Revalidate())
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            if (selectPublication is null || publish is null)
                return new(null, GitHubProposalPresentation.ContractError(identity, preflight.Operation, revision));
            var admitted = GitHubPublicationRequestFactory.Create(context, outcome, selectPublication(context.CurrentCheckpoint.State));
            if (!admitted.IsValid)
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            GitHubProposalProcessHooks.Reach("before-publication");
            context.CancellationToken.ThrowIfCancellationRequested();
            // Revalidate after candidate capture too; no ambient credential access occurs in H1.
            if (!preflight.Configuration.Revalidate() || !request.Revalidate())
                return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
            var observed = await publish(admitted.Authority!, admitted.Payload!, context.CancellationToken).ConfigureAwait(false);
            if (observed.Result.Kind == GitHubPublicationResultKind.Cancelled
                && FinalizationFailure(revision) is { } publicationDeadline)
                return new(null, publicationDeadline);
            return new(null, GitHubProposalPresentation.Publication(identity, preflight.Operation, revision, observed));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return new(null, FinalizationFailure(revision)
                ?? GitHubProposalPresentation.Local(identity, preflight.Operation, "cancelled", revision));
        }
        catch (ArgumentException)
        {
            return new(null, GitHubProposalPresentation.Local(identity, preflight.Operation, "local-invalid", revision));
        }
    }
}
