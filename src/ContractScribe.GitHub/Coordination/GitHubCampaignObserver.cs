using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

// Observation never supplies a claim, mutation entitlement, or permission to start work.
internal sealed class GitHubCampaignObserver : IDisposable
{
    private readonly GitHubApiClient client;
    private readonly GitHubCoordinationStore coordination;

    private GitHubCampaignObserver(GitHubApiClient client)
    { this.client = client; coordination = GitHubCoordinationStore.Create(client); }

    internal static GitHubCampaignObserver Create(ValidatedGitHubCampaignReadAuthority authority, string credential) =>
        new(GitHubApiClient.CreateReadOnly(authority, credential));

    internal async ValueTask<GitHubCampaignObservation> ObserveAsync(CancellationToken token = default)
    {
        var authority = client.ReadAuthority;
        var coordinationRef = GitHubPublicationFactory.CreateCoordinationRef(authority);
        var prefix = GitHubPublicationFactory.CreateProposalRefPrefix(authority);
        var campaign = coordinationRef.Split('/')[^1];
        GitHubRepositoryIdentity? repository = null;
        GitHubRef? target = null;
        GitHubRef? ledgerRef = null;
        GitHubCampaignLedger? ledger = null;
        var ledgerKind = GitHubCampaignLedgerKind.Unverifiable;
        var facts = new List<GitHubCampaignPullRequest>();
        var exhausted = false;
        var pages = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var cancellation = deadline.Token;
        try
        {
            var repoRead = await client.GetRepositoryAsync(cancellation).ConfigureAwait(false);
            if (repoRead.Value is null) return Transport(repoRead.Failure);
            repository = repoRead.Value.Identity;
            var targetRead = await client.GetRefAsync(authority.TargetRef, cancellation).ConfigureAwait(false);
            target = targetRead.Value;
            if (target is null) return RefFailure(targetRead.Failure, authority.TargetRef);
            var ledgerRead = await client.GetRefAsync(coordinationRef, cancellation).ConfigureAwait(false);
            ledgerRef = ledgerRead.Value;
            if (ledgerRef is null && ledgerRead.Failure?.Code != GitHubFailureCode.NotFound)
                return Transport(ledgerRead.Failure, coordinationRef);
            ledgerKind = ledgerRef is null ? GitHubCampaignLedgerKind.Missing : GitHubCampaignLedgerKind.Current;
            IGitHubCoordinationStateCapability? state = null;
            var generations = new List<GitHubCampaignGeneration>();
            if (ledgerRef is not null)
            {
                var read = await coordination.ReadObservedStateAsync(repository, target, ledgerRef.Oid, cancellation).ConfigureAwait(false);
                state = read.State;
                if (state is null)
                {
                    var kind = read.Failure?.Kind;
                    ledgerKind = kind == GitHubCoordinationFailureKind.Incompatible ? GitHubCampaignLedgerKind.Incompatible
                        : kind is GitHubCoordinationFailureKind.ObjectMismatch or GitHubCoordinationFailureKind.Bounds
                            ? GitHubCampaignLedgerKind.Corrupt : GitHubCampaignLedgerKind.Unverifiable;
                    return Fail(new(ledgerKind == GitHubCampaignLedgerKind.Incompatible ? GitHubCampaignFailureKind.Incompatible
                        : ledgerKind == GitHubCampaignLedgerKind.Corrupt ? GitHubCampaignFailureKind.Corrupt
                        : GitHubCampaignFailureKind.Transport, read.Failure?.TransportFailure, coordinationRef));
                }
                ledger = new(state.HeadOid, state.Stage, state.TargetCommitOid, state.GenerationId,
                    state.SnapshotCommitmentSha256, state.PolicyCommitmentSha256, state.OperationId,
                    state.OperationCommitmentSha256, state.CurrentCandidateCommitmentSha256,
                    coordination.ProposalRefFor(state), state.PullRequestNumber);
                generations.AddRange(coordination.ObservedGenerations(state));
                if (coordination.ObservedCurrentGeneration(state) is { } current
                    && !generations.Any(item => item.Ref == current.Ref)) generations.Add(current);
            }
            var collection = await client.ListPullRequestsAsync(cancellation).ConfigureAwait(false);
            if (collection.Value is not { Exhausted: true } set) return Transport(collection.Failure);
            exhausted = true;
            pages = set.Pages;
            var knownNumbers = generations.Where(item => item.Number is not null).Select(item => item.Number!.Value).ToHashSet();
            var currentRef = ledger?.ProposalRef;
            var currentCreation = generations.FirstOrDefault(item => item.Ref == currentRef)?.Metadata.CreationCommitment;
            var relevant = set.Items.Where(pr => Relevant(pr)).OrderBy(pr => pr.Number).ToArray();
            var details = new List<GitHubPullRequest>();
            foreach (var row in relevant)
            {
                var detail = await client.GetPullRequestAsync(row.Number, cancellation).ConfigureAwait(false);
                if (detail.Value is null)
                {
                    facts.Add(Project(row, false));
                    return Transport(detail.Failure, number: row.Number);
                }
                facts.Add(Project(detail.Value, false));
                if (!GitHubProposalPullRequestStore.Stable(row, detail.Value))
                    return Fail(new(GitHubCampaignFailureKind.StaleLedger, PullRequestNumber: row.Number));
                details.Add(detail.Value);
            }
            if (details.Count(pr => pr.Open) > 1) return Fail(new(GitHubCampaignFailureKind.MultipleActive));
            if (details.GroupBy(pr => pr.Head.Ref, StringComparer.Ordinal).Any(group => group.Count() > 1)
                || generations.GroupBy(item => item.Ref, StringComparer.Ordinal).Any(group => group.Count() > 1))
                return Fail(new(GitHubCampaignFailureKind.Ambiguous));
            var generationByRef = generations.ToDictionary(item => item.Ref, StringComparer.Ordinal);
            var refs = new Dictionary<string, GitHubRef>(StringComparer.Ordinal);
            // A recorded proposal ref is required even if the complete PR set is empty.
            if (state?.ProposalRefOid is { } proposalOid)
            {
                var reference = coordination.ProposalRefFor(state)!;
                var read = await client.GetRefAsync(reference, cancellation).ConfigureAwait(false);
                if (read.Value is null) return RefFailure(read.Failure, reference);
                refs.Add(reference, read.Value);
                if (read.Value.Oid != proposalOid) return Fail(new(GitHubCampaignFailureKind.RefChanged, Ref: reference));
            }
            foreach (var generation in generations.Where(item => item.Number is not null))
                if (!details.Any(pr => pr.Number == generation.Number))
                    return Fail(new(GitHubCampaignFailureKind.MissingPullRequest, PullRequestNumber: generation.Number));
            for (var index = 0; index < details.Count; index++)
            {
                var pr = details[index];
                var reference = "refs/heads/" + pr.Head.Ref;
                generationByRef.TryGetValue(reference, out var generation);
                if (generation is null || pr.Author != GitHubPublicationPrincipal.ActionsBot
                    || pr.Head.Repository != repository || pr.BaseRepository != repository
                    || "refs/heads/" + pr.BaseRef != authority.TargetRef
                    || pr.MaintainerCanModify != false || pr.Merged is null
                    || generation.Number is { } number && number != pr.Number
                    || pr.Head.Oid != generation.HeadOid || pr.BaseOid != generation.ObservedBaseOid
                    || pr.Title != generation.Metadata.Title || pr.Body != generation.Metadata.Body)
                    return Fail(new(GitHubCampaignFailureKind.Ownership, Ref: reference, PullRequestNumber: pr.Number));
                facts[index] = Project(pr, true);
                if (!refs.TryGetValue(reference, out var head))
                {
                    var read = await client.GetRefAsync(reference, cancellation).ConfigureAwait(false);
                    if (read.Value is null) return RefFailure(read.Failure, reference, pr.Number);
                    head = read.Value;
                    refs.Add(reference, head);
                }
                if (head.Oid != pr.Head.Oid) return Fail(new(GitHubCampaignFailureKind.RefChanged, Ref: reference, PullRequestNumber: pr.Number));
                if (generation.Stage is GitHubCoordinationStage.Merged or GitHubCoordinationStage.ClosedUnmerged
                    && (pr.Open || (generation.Stage == GitHubCoordinationStage.Merged) != pr.Merged.Value))
                    return Fail(new(GitHubCampaignFailureKind.StaleLedger, PullRequestNumber: pr.Number));
                if (pr.Open && pr.BaseOid != target.Oid)
                    return Fail(new(GitHubCampaignFailureKind.TargetMoved, Ref: authority.TargetRef, PullRequestNumber: pr.Number));
            }
            if (state is not null && details.All(pr => !pr.Open)
                && state.Stage is not (GitHubCoordinationStage.Merged or GitHubCoordinationStage.ClosedUnmerged)
                && state.TargetCommitOid != target.Oid)
                return Fail(new(GitHubCampaignFailureKind.TargetMoved, Ref: authority.TargetRef));

            // Close the observed read window. This is not a transactional snapshot or a claim.
            var finalTarget = await client.GetRefAsync(authority.TargetRef, cancellation).ConfigureAwait(false);
            if (finalTarget.Value is null) return RefFailure(finalTarget.Failure, authority.TargetRef);
            if (finalTarget.Value != target) return Fail(new(GitHubCampaignFailureKind.RefChanged, Ref: authority.TargetRef));
            var finalLedger = await client.GetRefAsync(coordinationRef, cancellation).ConfigureAwait(false);
            if (finalLedger.Value is null && finalLedger.Failure?.Code != GitHubFailureCode.NotFound)
                return Transport(finalLedger.Failure, coordinationRef);
            if (finalLedger.Value != ledgerRef) return Fail(new(GitHubCampaignFailureKind.RefChanged, Ref: coordinationRef));
            foreach (var pair in refs)
            {
                var read = await client.GetRefAsync(pair.Key, cancellation).ConfigureAwait(false);
                if (read.Value is null) return RefFailure(read.Failure, pair.Key);
                if (read.Value != pair.Value) return Fail(new(GitHubCampaignFailureKind.RefChanged, Ref: pair.Key));
            }
            foreach (var pr in details)
            {
                var read = await client.GetPullRequestAsync(pr.Number, cancellation).ConfigureAwait(false);
                if (read.Value is null) return Transport(read.Failure, number: pr.Number);
                if (read.Value != pr) return Fail(new(GitHubCampaignFailureKind.StaleLedger, PullRequestNumber: pr.Number));
            }
            var outcome = details.Any(pr => pr.Open) ? GitHubCampaignKind.Active
                : details.Any(pr => "refs/heads/" + pr.Head.Ref == currentRef) ? GitHubCampaignKind.Terminal
                : ledgerRef is null ? GitHubCampaignKind.FirstRun : GitHubCampaignKind.Unpublished;
            return Result(outcome);

            bool Relevant(GitHubPullRequest pr) => ("refs/heads/" + pr.Head.Ref).StartsWith(prefix, StringComparison.Ordinal)
                || pr.Body?.Contains("campaign=sha256:" + campaign, StringComparison.Ordinal) == true
                || knownNumbers.Contains(pr.Number)
                || currentCreation is not null && pr.Body?.Contains(currentCreation, StringComparison.Ordinal) == true;
        }
        catch (OperationCanceledException) { return Transport(new(token.IsCancellationRequested ? GitHubFailureCode.Cancelled : GitHubFailureCode.Timeout)); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Transport(new(GitHubFailureCode.HostFailure)); }

        GitHubCampaignObservation Result(GitHubCampaignKind kind, GitHubCampaignFailure? failure = null) =>
            new(kind, ledgerKind, repository, authority.TargetRef, target?.Oid, coordinationRef, ledgerRef?.Oid,
                ledger, facts.ToImmutableArray(), exhausted, pages, failure);
        GitHubCampaignObservation Fail(GitHubCampaignFailure failure) => Result(GitHubCampaignKind.Unverifiable, failure);
        GitHubCampaignObservation Transport(GitHubFailure? failure, string? reference = null, int? number = null) =>
            Fail(new(GitHubCampaignFailureKind.Transport,
                failure?.Code == GitHubFailureCode.Cancelled && !token.IsCancellationRequested && deadline.IsCancellationRequested
                    ? new(GitHubFailureCode.Timeout) : failure ?? new(GitHubFailureCode.InvalidResponse), reference, number));
        GitHubCampaignObservation RefFailure(GitHubFailure? failure, string reference, int? number = null) =>
            failure?.Code == GitHubFailureCode.NotFound
                ? Fail(new(GitHubCampaignFailureKind.MissingRef, failure, reference, number)) : Transport(failure, reference, number);
    }

    private static GitHubCampaignPullRequest Project(GitHubPullRequest pr, bool owned) =>
        new(pr.Id, pr.NodeId, pr.Number, pr.Open ? pr.Draft ? GitHubCampaignPullRequestKind.OpenDraft : GitHubCampaignPullRequestKind.OpenReady
            : pr.Merged == true ? GitHubCampaignPullRequestKind.Merged
            : pr.Merged == false ? GitHubCampaignPullRequestKind.ClosedUnmerged : GitHubCampaignPullRequestKind.Unknown,
            pr.Head, pr.BaseRepository, pr.BaseRef, pr.BaseOid, pr.Author, pr.CreatedAt, pr.ClosedAt, pr.MergedAt, owned);

    public void Dispose() => client.Dispose();
    public override string ToString() => nameof(GitHubCampaignObserver);
}
