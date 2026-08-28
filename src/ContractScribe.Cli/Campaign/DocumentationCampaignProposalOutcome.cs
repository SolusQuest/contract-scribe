using ContractScribe.Core;

namespace ContractScribe.Cli;

internal enum DocumentationCampaignProposalOutcomeKind
{
    NoWork, UnsupportedOnly, ProposalReady, RetryableStop, TerminalStop,
    Cancelled, TimedOut, BudgetExhausted, AmbiguousDispatch, StateConflict, HostContractError,
}

internal sealed class DocumentationCampaignProposalOutcome
{
    internal DocumentationCampaignProposalOutcome(
        DocumentationCampaignProposalOutcomeKind kind,
        string code,
        string? workItemKey = null,
        CampaignCheckpointArtifact? artifact = null)
    {
        if (!ValidCode(kind, code))
        {
            throw new ArgumentException("campaign.proposal.outcome-code-invalid", nameof(code));
        }

        Kind = kind;
        Code = code;
        WorkItemKey = workItemKey;
        Artifact = artifact;
    }

    internal DocumentationCampaignProposalOutcomeKind Kind { get; }
    internal string Code { get; }
    internal string? WorkItemKey { get; }
    internal CampaignCheckpointArtifact? Artifact { get; }
    internal CampaignTrustedProposal? TrustedProposal => WorkItemKey is null || Artifact is null
        ? null
        : Artifact.State.WorkItems.SingleOrDefault(item =>
            item.WorkItemKey == WorkItemKey && item.Status == CampaignWorkStatus.ProposalComplete)?.TrustedProposal;

    public override string ToString() => nameof(DocumentationCampaignProposalOutcome);

    private static bool ValidCode(DocumentationCampaignProposalOutcomeKind kind, string code) =>
        (kind, code) is
            (DocumentationCampaignProposalOutcomeKind.NoWork, "campaign.no-work")
            or (DocumentationCampaignProposalOutcomeKind.UnsupportedOnly, "campaign.unsupported-only")
            or (DocumentationCampaignProposalOutcomeKind.ProposalReady, "campaign.proposal.replay")
            or (DocumentationCampaignProposalOutcomeKind.ProposalReady, "campaign.proposal.ready")
            or (DocumentationCampaignProposalOutcomeKind.RetryableStop, "campaign.provider.retryable")
            or (DocumentationCampaignProposalOutcomeKind.TerminalStop, "campaign.terminal")
            or (DocumentationCampaignProposalOutcomeKind.Cancelled, "campaign.cancelled")
            or (DocumentationCampaignProposalOutcomeKind.TimedOut, "campaign.timed-out")
            or (DocumentationCampaignProposalOutcomeKind.BudgetExhausted, "campaign.exhausted")
            or (DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed")
            or (DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.observer")
            or (DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.settlement.unconfirmed")
            or (DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.checkpoint.unaccepted")
            or (DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.patch-active")
            or (DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.session-mismatch")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.invalid")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.reservation.foreign")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.request.invalid")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.runtime.mismatch")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.reservation.invalid")
            or (DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.preparation.invalid");
}
