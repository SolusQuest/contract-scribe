using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.PullRequests;

internal static class GitHubPublicationPrincipal
{
    internal static GitHubActor ActionsBot { get; } = new(
        41898282, "MDM6Qm90NDE4OTgyODI=", "github-actions[bot]", GitHubActorKind.Bot);

    internal static bool SameActor(GitHubActor? left, GitHubActor? right) =>
        left == right || IsKnownActionsBot(left) && IsKnownActionsBot(right);

    // Preserve the observed wire facts. REST and GraphQL spell this one bot's
    // login differently; only its exact immutable identity permits equivalence.
    private static bool IsKnownActionsBot(GitHubActor? actor) => actor is
    { Kind: GitHubActorKind.Bot, Login: "github-actions" or "github-actions[bot]" }
        && actor.Id == ActionsBot.Id && actor.NodeId == ActionsBot.NodeId;
}
