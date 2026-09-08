using System.Globalization;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Publication;

internal sealed record GitHubPublicationObservation(
    GitHubPublicationResult Result, string? OperationId, string? GenerationId, string? PullRequestUrl);

internal static class GitHubPublicationFacade
{
    internal static async Task<GitHubPublicationObservation> PublishAsync(
        ValidatedGitHubPublicationAuthority authority, ValidatedGitHubChangedFilePayload payload,
        Func<string?> readCredential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GitHubApiClient client;
        try
        {
            // The credential is transferred directly into R2, exactly once after H1 admission.
            client = GitHubApiClient.Create(authority, readCredential()!);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new(GitHubPublicationResult.FromRemoteFailure(token.IsCancellationRequested
                ? GitHubPublicationRemoteFailureKind.Cancelled : GitHubPublicationRemoteFailureKind.Permission), null, null, null);
        }
        GitHubPublicationObservation? selected = null;
        try
        {
            var reconciler = GitHubPublicationReconciler.Create(client,
                new GitHubActor(41898282, "MDM6Qm90NDE4OTgyODI=", "github-actions[bot]", GitHubActorKind.Bot));
            var result = await reconciler.PublishAsync(authority, payload, token).ConfigureAwait(false);
            // Only this same lifetime can carry R6's private PR-create entitlement.
            if (result.Kind == GitHubPublicationResultKind.RecoveredRefPartial)
            {
                result = await reconciler.PublishAsync(authority, payload, token).ConfigureAwait(false);
            }
            selected = Observe(authority, result, client.AuthenticatedRepository);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            selected ??= new(GitHubPublicationResult.FromRemoteFailure(
                token.IsCancellationRequested ? GitHubPublicationRemoteFailureKind.Cancelled
                    : GitHubPublicationRemoteFailureKind.HostFailure), null, null, null);
        }
        finally
        {
            try { client.Dispose(); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
        }
        return selected!;
    }

    internal static GitHubPublicationObservation Observe(
        ValidatedGitHubPublicationAuthority authority, GitHubPublicationResult result,
        GitHubRepositoryIdentity? repository)
    {
        string? operation = null;
        string? generation = null;
        long? number = null;
        if (result.StaleDraft is { } stale)
        {
            operation = stale.OperationId;
            generation = stale.GenerationId;
            number = stale.PullRequestNumber;
        }
        else if (result.PullRequest is { } pr)
        {
            operation = pr.OperationCommitmentSha256 == authority.OperationCommitmentSha256
                ? authority.OperationId : null;
            generation = pr.GenerationId;
            number = pr.Number;
        }
        else
        {
            var commitment = result.Claim?.OperationCommitmentSha256
                ?? result.ContentResidual?.OperationCommitmentSha256
                ?? result.RefResidual?.OperationCommitmentSha256;
            if (commitment == authority.OperationCommitmentSha256)
            {
                operation = authority.OperationId;
                generation = authority.GenerationId;
            }
        }
        var url = number is > 0 && repository is not null
            ? "https://github.com/" + repository.Owner + "/" + repository.Name + "/pull/"
                + number.Value.ToString(CultureInfo.InvariantCulture)
            : null;
        return new(result, operation, generation, url);
    }
}
