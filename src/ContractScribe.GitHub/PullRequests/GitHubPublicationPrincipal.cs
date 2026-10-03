using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.PullRequests;

internal static class GitHubPublicationPrincipal
{
    internal static GitHubActor ActionsBot { get; } = new(
        41898282, "MDM6Qm90NDE4OTgyODI=", "github-actions[bot]", GitHubActorKind.Bot);
}
