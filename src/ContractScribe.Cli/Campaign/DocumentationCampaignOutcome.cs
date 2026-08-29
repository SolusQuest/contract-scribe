using ContractScribe.Core;
using ContractScribe.Patching;

namespace ContractScribe.Cli;

internal enum DocumentationCampaignOutcomeKind
{
    Accepted,
    Reconstructed,
    Reduced,
    NoWork,
    Rejected,
    Stale,
    HostFailure,
    Cancelled,
    TimedOut,
    BudgetExhausted,
    StateConflict,
    AmbiguousDispatch,
    HostContractError,
    TerminalStop,
}

internal sealed record DocumentationCampaignOutcome
{
    internal DocumentationCampaignOutcome(
        DocumentationCampaignOutcomeKind kind,
        string code,
        CampaignCheckpointArtifact? artifact = null,
        DocumentationPatchAcceptedCandidate? acceptedCandidate = null)
    {
        if (acceptedCandidate is not null
            && kind is not (DocumentationCampaignOutcomeKind.Accepted
                or DocumentationCampaignOutcomeKind.Reconstructed))
        {
            throw new ArgumentException("Only an accepted outcome may carry a Patch candidate.", nameof(acceptedCandidate));
        }

        Kind = kind;
        Code = code;
        Artifact = artifact;
        AcceptedCandidate = acceptedCandidate;
    }

    internal DocumentationCampaignOutcomeKind Kind { get; }

    internal string Code { get; }

    internal CampaignCheckpointArtifact? Artifact { get; }

    internal DocumentationPatchAcceptedCandidate? AcceptedCandidate { get; }

    public override string ToString() =>
        $"{nameof(DocumentationCampaignOutcome)} {{ Kind = {Kind}, Code = {Code}, HasArtifact = {Artifact is not null}, HasCandidate = {AcceptedCandidate is not null} }}";
}
