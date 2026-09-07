using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.PullRequests;

internal enum GitHubProposalOutcome
{
    Absent, Appendable, HeldDraft, Ready, Merged, ClosedUnmerged,
    StaleDraft, Conflict, Unresolved, Failed,
}

internal sealed record GitHubProposalHead(string Ref, string CommitOid, string TreeOid) : GitHubValue;
internal sealed record GitHubProposalMetadata(string Title, string Body, string CreationCommitment,
    string MarkerHash, string CampaignKey) : GitHubValue;

internal interface IGitHubProposalObservation
{
    GitHubProposalOutcome Outcome { get; }
    GitHubPullRequest PullRequest { get; }
    GitHubProposalMetadata Metadata { get; }
    IGitHubCoordinationStateCapability Coordination { get; }
}

internal sealed record GitHubProposalResult(GitHubProposalOutcome Outcome,
    IGitHubProposalObservation? Observation = null, GitHubFailure? Failure = null,
    GitHubDelivery Delivery = GitHubDelivery.Read) : GitHubValue;

// R6 chooses an explicit operation. Recovery is deliberately incapable of creating a PR.
internal sealed class GitHubProposalPullRequestStore
{
    private readonly GitHubApiClient client;
    private readonly GitHubCoordinationStore coordination;
    private readonly GitHubActor publisher;
    private readonly TimeSpan recoveryTimeout = TimeSpan.FromSeconds(90);

    private GitHubProposalPullRequestStore(GitHubApiClient client,
        GitHubCoordinationStore coordination, GitHubActor publisher)
    { this.client = client; this.coordination = coordination; this.publisher = publisher; }

    internal static GitHubProposalPullRequestStore Create(GitHubApiClient client,
        GitHubCoordinationStore coordination, GitHubActor publisher)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(coordination);
        if (!coordination.UsesClient(client) || publisher is null || publisher.Id <= 0
            || string.IsNullOrEmpty(publisher.NodeId) || publisher.NodeId.Length > 256
            || string.IsNullOrEmpty(publisher.Login) || publisher.Login.Length > 256
            || publisher.Kind is not (GitHubActorKind.Bot or GitHubActorKind.User))
            throw new ArgumentException("Invalid publication principal or store binding.");
        return new(client, coordination, publisher);
    }

    internal GitHubProposalMetadata? Metadata(IGitHubCoordinationStateCapability state)
    {
        var source = coordination.CreationSource(state);
        var reference = coordination.ProposalRefFor(state);
        var commitment = coordination.CreationCommitment(state);
        if (source is null || reference is null || commitment is null) return null;
        var campaign = GitHubPublicationFactory.CreateCoordinationRef(client.Authority).Split('/')[^1];
        var generation = reference.Split('/')[^1];
        var marker = "<!-- contract-scribe-publication-v1 ownership=sha256:" + commitment + " -->\n";
        var body = marker + "campaign=sha256:" + campaign + "\n"
            + "generation=sha256:" + generation + "\n"
            + "snapshot=sha256:" + source.SnapshotCommitmentSha256 + "\n"
            + "policy=sha256:" + source.PolicyCommitmentSha256 + "\n"
            + "headRef=sha256:" + Hash(reference) + "\n"
            + "baseRef=sha256:" + Hash(source.TargetRef) + "\n"
            + "creationOperationId=sha256:" + source.OperationCommitmentSha256 + "\n";
        return new("ContractScribe proposal " + generation, body, commitment,
            GitHubCoordinationCodec.MarkerHash(commitment), campaign);
    }

    internal ValueTask<GitHubProposalResult> ObserveAsync(IGitHubCoordinationStateCapability state,
        GitHubProposalHead expected, IGitHubProposalObservation? previous = null,
        CancellationToken cancellationToken = default) => ObserveCoreAsync(state, expected, previous, false, cancellationToken);

    internal ValueTask<GitHubProposalResult> RecoverAsync(IGitHubCoordinationStateCapability state,
        GitHubProposalHead expected, IGitHubProposalObservation? previous = null,
        CancellationToken cancellationToken = default) => ObserveCoreAsync(state, expected, previous, true, cancellationToken);

    internal async ValueTask<GitHubProposalResult> CreateAsync(IGitHubCoordinationStateCapability state,
        GitHubProposalHead expected, CancellationToken cancellationToken = default)
    {
        if (!Input(state, expected) || state.Stage != GitHubCoordinationStage.ProposalRefAdvanced
            || state.Transition == "same-snapshot-append"
            || state.OperationCommitmentSha256 != client.Authority.OperationCommitmentSha256)
            return Conflict();
        var observed = await ObserveCoreAsync(state, expected, null, false, cancellationToken).ConfigureAwait(false);
        if (observed.Outcome != GitHubProposalOutcome.Absent) return observed;
        var metadata = Metadata(state)!;
        var claim = await coordination.ReadClaimAsync(state, cancellationToken).ConfigureAwait(false);
        if (claim.Guard is null) return ClaimFailure(claim);
        var target = await client.GetRefAsync(state.TargetRef, cancellationToken).ConfigureAwait(false);
        if (target.Value is null) return Failed(target);
        if (target.Value.Oid != state.TargetCommitOid) return Conflict();
        var created = await client.CreatePullRequestAsync(new(metadata.CreationCommitment, expected.Ref,
            expected.CommitOid, state.TargetRef, state.TargetCommitOid, metadata.Title, metadata.Body), cancellationToken).ConfigureAwait(false);
        if (created.Delivery == GitHubDelivery.NotDispatched) return Failed(created);
        // An uncertain POST is never retried. Recovery has its own bounded cancellation scope.
        using var recovery = new CancellationTokenSource(recoveryTimeout);
        var result = await ObserveCoreAsync(state, expected, null, true, recovery.Token).ConfigureAwait(false);
        if (recovery.IsCancellationRequested)
            return new(GitHubProposalOutcome.Failed, Failure: new(GitHubFailureCode.Timeout), Delivery: created.Delivery);
        if (created.Value is { } receipt && result.Observation is { } proof
            && (receipt.Id != proof.PullRequest.Id || receipt.NodeId != proof.PullRequest.NodeId
                || receipt.Number != proof.PullRequest.Number)) return Conflict();
        return result with { Delivery = created.Delivery };
    }

    private async ValueTask<GitHubProposalResult> ObserveCoreAsync(IGitHubCoordinationStateCapability state,
        GitHubProposalHead expected, IGitHubProposalObservation? previous, bool recovering,
        CancellationToken cancellationToken)
    {
        if (!Input(state, expected) || previous is not null
            && (previous is not Observation prior || !ReferenceEquals(prior.Owner, this))) return Conflict();
        var current = await coordination.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.State is null) return ClaimFailure(current);
        if (current.State.HeadOid != state.HeadOid || current.State.Repository != state.Repository) return Conflict();
        state = current.State;
        var metadata = Metadata(state);
        if (metadata is null) return Conflict();
        var proposal = await VerifyProposalAsync(state, expected, cancellationToken).ConfigureAwait(false);
        if (proposal is not null) return proposal;
        var collection = await client.ListPullRequestsAsync(cancellationToken).ConfigureAwait(false);
        if (collection.Value is not { Exhausted: true } set) return Failed(collection);
        var prefix = expected.Ref[..(expected.Ref.LastIndexOf('/') + 1)];
        var candidates = new List<GitHubPullRequest>();
        var active = 0;
        foreach (var item in set.Items)
        {
            var head = "refs/heads/" + item.Head.Ref;
            var relevant = head.StartsWith(prefix, StringComparison.Ordinal)
                || item.Body?.Contains("campaign=sha256:" + metadata.CampaignKey, StringComparison.Ordinal) == true
                || item.Body?.Contains(metadata.CreationCommitment, StringComparison.Ordinal) == true
                || item.Number == state.PullRequestNumber
                || item.Number == coordination.CreationSource(state)?.PullRequestNumber
                || item.Number == previous?.PullRequest.Number;
            if (!relevant) continue;
            var detail = await client.GetPullRequestAsync(item.Number, cancellationToken).ConfigureAwait(false);
            if (detail.Value is null) return Failed(detail);
            var pr = detail.Value;
            if (!Stable(item, pr)) return Conflict();
            if (pr.Open && ++active > 1) return Conflict();
            if (head != expected.Ref)
            {
                // Another active generation or colliding ownership never permits creation.
                if (pr.Open || pr.Body?.Contains(metadata.CreationCommitment, StringComparison.Ordinal) == true)
                    return Conflict();
                continue;
            }
            candidates.Add(pr);
        }
        if (candidates.Count > 1) return Conflict();
        if (candidates.Count == 0)
        {
            if (active != 0 || state.PullRequestNumber is not null
                || coordination.CreationSource(state)?.PullRequestNumber is not null || previous is not null)
                return Conflict();
            return new(recovering ? GitHubProposalOutcome.Unresolved : GitHubProposalOutcome.Absent);
        }
        var candidate = candidates[0];
        if (active > (candidate.Open ? 1 : 0)) return Conflict();
        var classified = Classify(state, expected, metadata, candidate, previous);
        if (classified.Observation is null) return classified;
        // Close the read window against a changed coordination/proposal authority.
        var final = await coordination.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (final.State is null) return ClaimFailure(final);
        if (final.State.HeadOid != state.HeadOid || final.State.Repository != state.Repository) return Conflict();
        proposal = await VerifyProposalAsync(state, expected, cancellationToken).ConfigureAwait(false);
        if (proposal is not null) return proposal;
        var finalTarget = await client.GetRefAsync(state.TargetRef, cancellationToken).ConfigureAwait(false);
        if (finalTarget.Value is null) return Failed(finalTarget);
        if (candidate.Open && finalTarget.Value.Oid != state.TargetCommitOid
            && classified.Outcome != GitHubProposalOutcome.StaleDraft) return Conflict();
        var confirmed = await client.GetPullRequestAsync(candidate.Number, cancellationToken).ConfigureAwait(false);
        if (confirmed.Value is null) return Failed(confirmed);
        if (confirmed.Value != candidate) return Conflict();
        return classified;
    }

    private GitHubProposalResult Classify(IGitHubCoordinationStateCapability state, GitHubProposalHead expected,
        GitHubProposalMetadata metadata, GitHubPullRequest pr, IGitHubProposalObservation? previous)
    {
        var source = coordination.CreationSource(state)!;
        if (pr.Author != publisher || pr.Head.Repository != state.Repository || pr.BaseRepository != state.Repository
            || "refs/heads/" + pr.Head.Ref != expected.Ref || pr.Head.Oid != expected.CommitOid
            || "refs/heads/" + pr.BaseRef != state.TargetRef || pr.Title != metadata.Title || pr.Body != metadata.Body
            || pr.MaintainerCanModify != false || pr.Merged is null
            || source.PullRequestNumber is { } number && pr.Number != number
            || previous is not null && (pr.Id != previous.PullRequest.Id || pr.NodeId != previous.PullRequest.NodeId
                || pr.Number != previous.PullRequest.Number || pr.CreatedAt != previous.PullRequest.CreatedAt)) return Conflict();
        if (previous is not null && ((!previous.PullRequest.Open && pr.Open)
            || !previous.PullRequest.Draft && pr.Draft)) return Conflict();
        if (state.Stage is GitHubCoordinationStage.Merged or GitHubCoordinationStage.ClosedUnmerged
            && (pr.Open || (state.Stage == GitHubCoordinationStage.Merged) != pr.Merged.Value)) return Conflict();
        var retainedStaleBase = StaleBase(state) ?? StaleBase(source)
            ?? (previous?.PullRequest.BaseOid is { } priorBase && priorBase != state.TargetCommitOid ? priorBase : null);
        if (!pr.Open)
        {
            // Terminal ownership follows the authenticated base observation, including a stale draft's base.
            if (pr.BaseOid != (retainedStaleBase ?? state.TargetCommitOid)) return Conflict();
            return Observed(pr.Merged.Value ? GitHubProposalOutcome.Merged : GitHubProposalOutcome.ClosedUnmerged);
        }
        if (retainedStaleBase is not null)
        {
            if (pr.BaseOid != retainedStaleBase || !pr.Draft) return Conflict();
            return Observed(GitHubProposalOutcome.StaleDraft);
        }
        if (pr.BaseOid != state.TargetCommitOid)
        {
            var retainedSuccess = previous?.PullRequest.BaseOid == state.TargetCommitOid
                || SuccessfulBase(state) || SuccessfulBase(source);
            if (!pr.Draft || retainedSuccess) return Conflict();
            return Observed(GitHubProposalOutcome.StaleDraft);
        }
        if (!pr.Draft) return Observed(GitHubProposalOutcome.Ready);
        return Observed(state.Stage == GitHubCoordinationStage.AwaitingReview
            ? GitHubProposalOutcome.HeldDraft : GitHubProposalOutcome.Appendable);

        GitHubProposalResult Observed(GitHubProposalOutcome outcome) =>
            new(outcome, new Observation(this, outcome, pr, metadata, state));
        string? StaleBase(IGitHubCoordinationStateCapability evidence) =>
            evidence.PullRequestNumber is not null && evidence.ExpectedBaseOid == state.TargetCommitOid
                && evidence.ObservedBaseOid != state.TargetCommitOid ? evidence.ObservedBaseOid : null;
        bool SuccessfulBase(IGitHubCoordinationStateCapability evidence) =>
            evidence.PullRequestNumber is not null && evidence.ExpectedBaseOid == state.TargetCommitOid
                && evidence.ObservedBaseOid == state.TargetCommitOid;
    }

    private bool Input(IGitHubCoordinationStateCapability? state, GitHubProposalHead? expected) =>
        state is not null && expected is not null && coordination.Owns(state)
        && expected.Ref == coordination.ProposalRefFor(state)
        && expected.CommitOid == state.ProposalCommitOid && expected.CommitOid == state.ProposalRefOid
        && expected.TreeOid == state.ProposalTreeOid && state.ProposalCommitOid is not null;

    private async ValueTask<GitHubProposalResult?> VerifyProposalAsync(IGitHubCoordinationStateCapability state,
        GitHubProposalHead expected, CancellationToken cancellationToken)
    {
        var reference = await client.GetRefAsync(expected.Ref, cancellationToken).ConfigureAwait(false);
        if (reference.Value is null) return Failed(reference);
        if (reference.Value.Oid != expected.CommitOid) return Conflict();
        var commit = await client.GetCommitAsync(expected.CommitOid, cancellationToken).ConfigureAwait(false);
        if (commit.Value is null) return Failed(commit);
        if (commit.Value.TreeOid != expected.TreeOid) return Conflict();
        var tree = await client.GetTreeAsync(expected.TreeOid, cancellationToken).ConfigureAwait(false);
        if (tree.Value is null) return Failed(tree);
        var target = await client.GetRefAsync(state.TargetRef, cancellationToken).ConfigureAwait(false);
        if (target.Value is null) return Failed(target);
        // A merged PR may legitimately advance the target. Eligibility is checked after classification.
        return null;
    }

    private static bool Stable(GitHubPullRequest list, GitHubPullRequest detail) =>
        list.Id == detail.Id && list.NodeId == detail.NodeId && list.Number == detail.Number
        && list.Open == detail.Open && list.Draft == detail.Draft && list.MergedAt == detail.MergedAt
        && list.ClosedAt == detail.ClosedAt && list.CreatedAt == detail.CreatedAt
        && list.Title == detail.Title && list.Body == detail.Body && list.Author == detail.Author
        && list.Head == detail.Head && list.BaseRepository == detail.BaseRepository
        && list.BaseRef == detail.BaseRef && list.BaseOid == detail.BaseOid
        && (list.Merged is null || list.Merged == detail.Merged)
        && (list.MaintainerCanModify is null || list.MaintainerCanModify == detail.MaintainerCanModify);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static GitHubProposalResult Conflict() => new(GitHubProposalOutcome.Conflict);
    private static GitHubProposalResult ClaimFailure(GitHubCoordinationResult result) =>
        new(result.Failure?.Kind == GitHubCoordinationFailureKind.Transport ? GitHubProposalOutcome.Failed : GitHubProposalOutcome.Conflict,
            Failure: result.Failure?.TransportFailure);
    private static GitHubProposalResult Failed<T>(GitHubApiResult<T> result) where T : class =>
        new(GitHubProposalOutcome.Failed, Failure: result.Failure, Delivery: result.Delivery);

    private sealed class Observation(GitHubProposalPullRequestStore owner, GitHubProposalOutcome outcome,
        GitHubPullRequest pullRequest, GitHubProposalMetadata metadata, IGitHubCoordinationStateCapability state)
        : IGitHubProposalObservation
    {
        internal GitHubProposalPullRequestStore Owner { get; } = owner;
        public GitHubProposalOutcome Outcome { get; } = outcome;
        public GitHubPullRequest PullRequest { get; } = pullRequest;
        public GitHubProposalMetadata Metadata { get; } = metadata;
        public IGitHubCoordinationStateCapability Coordination { get; } = state;
        public override string ToString() => nameof(Observation);
    }
}
