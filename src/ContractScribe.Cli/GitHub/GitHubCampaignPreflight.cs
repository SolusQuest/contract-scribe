using System.Security.Cryptography;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Cli;

internal sealed record GitHubCampaignPreflightLink(string? GenerationId, string Url);

internal static class GitHubCampaignPreflight
{
    internal static CliExecutionResult? Select(CliBuildIdentity identity, CampaignOperation operation,
        GitHubProposalRequest request, GitHubCampaignObservation observed, long? revision)
    {
        var pr = observed.PullRequests.FirstOrDefault(row => row.Number == observed.Failure?.PullRequestNumber)
            ?? observed.PullRequests.FirstOrDefault(row => row.Kind is GitHubCampaignPullRequestKind.OpenDraft or GitHubCampaignPullRequestKind.OpenReady)
            ?? observed.PullRequests.FirstOrDefault(row => row.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged
                && row.OriginalBaseOid == observed.TargetOid)
            ?? observed.PullRequests.FirstOrDefault();
        GitHubCampaignPreflightLink? link = observed.Repository is { } repo && pr is { OwnershipVerified: true }
            ? new(pr.GenerationId, $"https://github.com/{repo.Owner}/{repo.Name}/pull/{pr.Number}") : null;
        CliExecutionResult Stop(string outcome, string reason) =>
            GitHubProposalPresentation.Preflight(identity, operation, outcome, reason, revision, link);
        if (observed.Failure is { } failure)
        {
            if (failure.Kind == GitHubCampaignFailureKind.TargetMoved) return Stop("stale", "changed-base");
            if (failure.Kind is GitHubCampaignFailureKind.MultipleActive or GitHubCampaignFailureKind.Ambiguous)
                return Stop("conflict", "ambiguous-active-work");
            if (failure.Kind == GitHubCampaignFailureKind.Transport)
                return Stop(TransportOutcome(failure.Transport), TransportOutcome(failure.Transport) is "host-failure"
                    ? "unverified-lifecycle" : TransportOutcome(failure.Transport));
            return Stop("human-change", "unsafe-lifecycle");
        }
        if (!observed.CollectionExhausted || observed.TargetOid is null
            || observed.Kind == GitHubCampaignKind.Unverifiable)
            return Stop("human-change", "unverified-lifecycle");
        if (observed.Ledger?.Stage == GitHubCoordinationStage.StaleDraft)
            return Stop("stale", "stale-publication");
        if (observed.PullRequests.Any(row => row.Kind is GitHubCampaignPullRequestKind.OpenDraft or GitHubCampaignPullRequestKind.OpenReady))
            return NeedsPublicationRecovery(observed) ? null : Stop("awaiting-review", "awaiting-review");
        if (observed.PullRequests.Any(row => row.Kind == GitHubCampaignPullRequestKind.ClosedUnmerged
            && row.OriginalBaseOid == observed.TargetOid))
            return Stop("closed-unmerged", "closed-same-base");
        if (NeedsPublicationRecovery(observed)) return null;
        if (observed.Ledger?.Stage == GitHubCoordinationStage.PullRequestCreateStarted)
            return Stop("conflict", "publication-incomplete");
        // An incomplete publication cannot be replaced by a changed caller operation/base.
        if (observed.Ledger is
            {
                Stage: GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated
                or GitHubCoordinationStage.ProposalRefAdvanced or GitHubCoordinationStage.PullRequestCreateStarted
            } ledger
            && (ledger.OperationId != request.GitHub.OperationId || ledger.OriginalBaseOid != observed.TargetOid))
            return Stop("conflict", "publication-incomplete");
        return null;
    }

    internal static bool NeedsPublicationRecovery(GitHubCampaignObservation observed) =>
        observed.Failure is null && observed.Ledger is
        { Stage: GitHubCoordinationStage.PullRequestCreateStarted or GitHubCoordinationStage.PullRequestCreated } ledger
        && observed.PullRequests.Any(row => row.OwnershipVerified && row.GenerationId == ledger.GenerationId
            && "refs/heads/" + row.Head.Ref == ledger.ExpectedProposalRef);

    internal static string TransportOutcome(GitHubFailure? failure) => failure?.Code switch
    {
        GitHubFailureCode.Authentication or GitHubFailureCode.Permission => "permission",
        GitHubFailureCode.RateLimit => "rate-limit",
        GitHubFailureCode.Cancelled => "cancelled",
        GitHubFailureCode.Timeout => "timeout",
        GitHubFailureCode.Conflict => "conflict",
        _ => "host-failure",
    };

    internal static string SnapshotBinding(CampaignPreflightResult preflight, GitHubProposalRequest request,
        GitHubCampaignObservation observed)
    {
        var configuration = preflight.Configuration.Document;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "github-campaign-snapshot-v1",
            actualBase = observed.TargetOid,
            callerSnapshot = request.Snapshot,
            preflight.InputIdentity,
            policy = Convert.ToHexStringLower(SHA256.HashData(preflight.PolicyBytes)),
            configuration.Planning,
            configuration.Retry,
            configuration.ScribeRequest,
            campaignCorrectness = new
            {
                configuration.Budgets.Campaign.MaximumAttemptsPerTarget,
                configuration.Budgets.Campaign.MaximumCandidatesPerBlock,
            },
            contextLimits = configuration.Budgets.Scribe,
            provider = configuration.Provider.ExactProjection,
            configuration.CostPolicy,
            terminal = TerminalSeed(observed),
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static string TerminalSeed(GitHubCampaignObservation observed) =>
        JsonSerializer.Serialize(observed.PullRequests
            .Where(row => row.Kind is GitHubCampaignPullRequestKind.Merged or GitHubCampaignPullRequestKind.ClosedUnmerged)
            .OrderBy(row => row.Number)
            .Select(row => new { row.Number, row.GenerationId, row.OriginalBaseOid, row.Kind }));
}
