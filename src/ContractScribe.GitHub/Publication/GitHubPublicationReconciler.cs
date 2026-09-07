using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;
using GitResult = ContractScribe.GitHub.GitData.GitHubProposalResult;
using PrResult = ContractScribe.GitHub.PullRequests.GitHubProposalResult;
using PrOutcome = ContractScribe.GitHub.PullRequests.GitHubProposalOutcome;

namespace ContractScribe.GitHub.Publication;

// One authority-bound lifetime can carry acknowledged write rights between bounded calls.
// Reconstructing this adapter intentionally reconstructs observations, never those rights.
internal sealed class GitHubPublicationReconciler : IGitHubPublicationPort
{
    private readonly GitHubApiClient client;
    private readonly GitHubCoordinationStore coordination;
    private readonly GitHubProposalStore git;
    private readonly GitHubProposalPullRequestStore prs;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IGitHubPullRequestEntitlement? createRight;
    private IGitHubProposalObservation? previous;
    private ValidatedGitHubPublicationAuthority Authority => client.Authority;

    private GitHubPublicationReconciler(GitHubApiClient client, GitHubActor publisher)
    {
        this.client = client;
        coordination = GitHubCoordinationStore.Create(client);
        git = GitHubProposalStore.Create(client, coordination);
        prs = GitHubProposalPullRequestStore.Create(client, coordination, publisher);
    }

    // Publisher is supplied by trusted host composition, independently of inspected PRs.
    // Token-kind/platform proof is host-owned; required-permission headers are not grants.
    internal static GitHubPublicationReconciler Create(GitHubApiClient client, GitHubActor publisher) => new(client, publisher);

    public async ValueTask<GitHubPublicationResult> PublishAsync(ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default)
    {
        if (!Correlates(authority, payload))
            return GitHubPublicationResult.LocalInvalid(GitHubPublicationValidationCode.InvalidCorrelation,
                GitHubPublicationFieldId.Payload);
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Reconcile(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Failure(GitHubPublicationRemoteFailureKind.Cancelled); }
        catch { return Failure(GitHubPublicationRemoteFailureKind.HostFailure); }
        finally { if (entered) gate.Release(); }
    }

    private bool Correlates(ValidatedGitHubPublicationAuthority? authority, ValidatedGitHubChangedFilePayload? payload)
    {
        if (authority is null || payload is null || authority.AuthorityCommitmentSha256 != Authority.AuthorityCommitmentSha256
            || authority.OperationCommitmentSha256 != Authority.OperationCommitmentSha256
            || payload.AuthorityCommitmentSha256 != authority.AuthorityCommitmentSha256
            || payload.Files.Length != authority.ChangedFiles.Length) return false;
        long bytes = 0;
        for (var i = 0; i < payload.Files.Length; i++)
        {
            var file = payload.Files[i];
            if (file.CandidateBytes.IsDefault || file.Path != authority.ChangedFiles[i].Path
                || file.CandidateBytes.Length > GitHubPublicationContract.MaximumPayloadBytesPerFile
                || Convert.ToHexStringLower(SHA256.HashData(file.CandidateBytes.AsSpan())) != authority.ChangedFiles[i].CandidateFileSha256)
                return false;
            bytes += file.CandidateBytes.Length;
        }
        return bytes <= GitHubPublicationContract.MaximumAggregatePayloadBytes;
    }

    private async ValueTask<GitHubPublicationResult> Reconcile(ValidatedGitHubChangedFilePayload payload, CancellationToken token)
    {
        var repository = await client.GetRepositoryAsync(token).ConfigureAwait(false);
        if (repository.Value is null) return Transport(repository.Failure);
        if (repository.Value.Archived || repository.Value.Disabled) return Failure(GitHubPublicationRemoteFailureKind.Permission);
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null) return Project(read);
        var state = read.State;
        if (state is not null && !SameOperation(state))
        {
            if (state.OperationId == Authority.OperationId
                || state.Stage is GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated)
                return Failure(GitHubPublicationRemoteFailureKind.Conflict);
            var old = await git.InspectPredecessorAsync(state, token).ConfigureAwait(false);
            if (old.Inspection is null) return Project(old);
            var observed = old.Inspection.ContentExists && state.ProposalCommitOid is not null
                ? await prs.ObserveVerifiedAsync(state, old.Inspection, previous, token: token).ConfigureAwait(false)
                : await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
            if (observed.Outcome is PrOutcome.Failed or PrOutcome.Conflict or PrOutcome.Unresolved) return Project(observed);
            previous = observed.Observation;
            if (observed.Outcome == PrOutcome.StaleDraft) return StaleDraft(observed.Observation!);
            if (observed.Outcome is PrOutcome.Ready or PrOutcome.HeldDraft) return Hold(observed.Observation!);
            if (observed.Outcome is PrOutcome.Merged or PrOutcome.ClosedUnmerged)
            {
                if (!SuccessorMatches(state, observed.Observation!)) return Lifecycle(observed.Observation!);
                if (coordination.Inspect(read.Read)!.TargetOid != Authority.ExpectedBaseCommitOid)
                    return Failure(GitHubPublicationRemoteFailureKind.Stale);
                var terminal = observed.Outcome == PrOutcome.Merged ? GitHubCoordinationStage.Merged : GitHubCoordinationStage.ClosedUnmerged;
                if (state.Stage != terminal)
                {
                    var recorded = await coordination.RecordObservationAsync(observed.Observation!, token).ConfigureAwait(false);
                    if (recorded.State is null) return Project(recorded);
                    read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
                    if (read.Read is null || read.State is null) return Project(read);
                    state = read.State;
                    old = await git.InspectPredecessorAsync(state, token).ConfigureAwait(false);
                    if (old.Inspection is null) return Project(old);
                    observed = await prs.ObserveVerifiedAsync(state, old.Inspection, previous, token: token).ConfigureAwait(false);
                    if (observed.Observation is null) return Project(observed);
                    if (!SuccessorMatches(state, observed.Observation)) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
                }
            }
            else if (Authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
            {
                if (observed.Outcome != PrOutcome.Appendable || state.Stage != GitHubCoordinationStage.Published
                    || !AppendIdentityMatches(state)) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
                if (state.PolicyCommitmentSha256 != Authority.PolicyCommitmentSha256 || Exhausted(state))
                    return Hold(observed.Observation!);
            }
            else if (!(Authority.Transition == GitHubPublicationTransitionKind.Initial
                && state.Stage == GitHubCoordinationStage.Stale && Authority.GenerationId != state.GenerationId
                && observed.Outcome == PrOutcome.Absent)) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        }

        if (state is null || !SameOperation(state))
        {
            var preflight = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
            if (preflight.Inspection is null) return Project(preflight);
            if (Authority.Transition != GitHubPublicationTransitionKind.SameSnapshotAppend)
            {
                if (preflight.Inspection.ObservedRefOid != GitHubPublicationContract.MissingGitObjectId)
                    return Failure(GitHubPublicationRemoteFailureKind.Conflict);
                var campaign = await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
                if (campaign.Outcome != PrOutcome.Absent) return Project(campaign);
                previous = null;
            }
            var claim = await coordination.ClaimAsync(read.Read, token).ConfigureAwait(false);
            if (claim.State is null) return Project(claim);
            state = claim.State;
            if (!SameOperation(state)) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        }

        // Discover actual content/ref/PR residuals before any stale-stage mutation.
        read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read);
        state = read.State;
        if (!SameOperation(state)) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        var inspected = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (inspected.Inspection is null) return Project(inspected);
        var proof = inspected.Inspection;
        if (state.Stage == GitHubCoordinationStage.Claimed && proof.ObservedRefOid == proof.CommitOid)
            return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        var hasPrContext = state.ProposalCommitOid is not null || state.Transition == "same-snapshot-append";
        var pr = hasPrContext
            ? await prs.ObserveVerifiedAsync(state, proof, previous, token: token).ConfigureAwait(false)
            : await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
        if (pr.Outcome is PrOutcome.Failed or PrOutcome.Conflict or PrOutcome.Unresolved) return Project(pr);
        previous = pr.Observation ?? previous;
        if (pr.Observation is not null && pr.Outcome is not PrOutcome.Appendable)
            return await Complete(state, pr.Observation, payload, token).ConfigureAwait(false);
        if (state.Stage is GitHubCoordinationStage.Stale or GitHubCoordinationStage.StaleDraft)
            return Failure(GitHubPublicationRemoteFailureKind.Stale);
        if (coordination.Inspect(read.Read)!.TargetOid != Authority.ExpectedBaseCommitOid)
        {
            var update = GitHubCoordinationStageUpdate.Stale(Authority.ExpectedBaseCommitOid, coordination.Inspect(read.Read)!.TargetOid);
            if (proof.ContentExists && state.Stage is GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated)
            {
                if (state.Stage == GitHubCoordinationStage.Claimed) update = GitHubCoordinationStageUpdate.ContentCreated(proof.CommitOid);
                else if (proof.ObservedRefOid == proof.CommitOid) update = GitHubCoordinationStageUpdate.ProposalRefAdvanced(proof.CommitOid, proof.TreeOid);
            }
            var stale = await coordination.AdvanceAsync(state, update, token).ConfigureAwait(false);
            if (stale.State is null) return Project(stale);
            return Failure(GitHubPublicationRemoteFailureKind.Stale);
        }
        return state.Stage switch
        {
            GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated =>
                await AdvanceProposal(state, payload, token).ConfigureAwait(false),
            GitHubCoordinationStage.ProposalRefAdvanced =>
                await FinishProposal(state, proof, pr, payload, allowCreate: true, token).ConfigureAwait(false),
            GitHubCoordinationStage.PullRequestCreated or GitHubCoordinationStage.Published or GitHubCoordinationStage.AwaitingReview
                when pr.Observation is not null => await Complete(state, pr.Observation, payload, token).ConfigureAwait(false),
            _ => Failure(GitHubPublicationRemoteFailureKind.Conflict),
        };
    }

    private async ValueTask<GitHubPublicationResult> AdvanceProposal(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, CancellationToken token)
    {
        var prepared = await git.PrepareAsync(state, payload, token).ConfigureAwait(false);
        if (prepared.Prepared is null) return Project(prepared);
        var content = await git.CreateContentAsync(prepared.Prepared, state, token).ConfigureAwait(false);
        if (content.Failure is not null || content.Content is null) return Project(content);
        IGitHubProposalRefEntitlement? right = null;
        if (state.Stage == GitHubCoordinationStage.Claimed)
        {
            var recorded = await coordination.AdvanceAsync(state,
                GitHubCoordinationStageUpdate.ContentCreated(content.Content.CommitOid), token).ConfigureAwait(false);
            if (recorded.State is null) return Project(recorded);
            state = recorded.State;
            if (state.Stage == GitHubCoordinationStage.Stale) return Failure(GitHubPublicationRemoteFailureKind.Stale);
            right = recorded.ProposalRefEntitlement;
        }
        // The active PR may have changed while immutable objects were materialized.
        if (state.Transition == "same-snapshot-append")
        {
            var current = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
            if (current.Read is null) return Project(current);
            var inspected = await git.InspectAsync(current.Read, payload, token).ConfigureAwait(false);
            if (inspected.Inspection is null) return Project(inspected);
            var observed = await prs.ObserveVerifiedAsync(state, inspected.Inspection, previous, token: token).ConfigureAwait(false);
            if (observed.Observation is null) return Project(observed);
            previous = observed.Observation;
            if (observed.Outcome != PrOutcome.Appendable) return Lifecycle(observed.Observation);
        }
        var advanced = await git.AdvanceRefAsync(content.Content, state, right, token).ConfigureAwait(false);
        if (advanced.Failure is not null)
        {
            if (advanced.Failure.Kind == GitHubProposalFailureKind.Unresolved && advanced.Failure.Transport is null
                && advanced.Failure.Readback is null)
                return GitHubPublicationResult.RecoveredContentPartial(new(GitHubPublicationResourceKind.Commit,
                    content.Content.CommitOid, Authority.OperationCommitmentSha256));
            return Project(advanced);
        }
        var result = await coordination.AdvanceAsync(state,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.Content.CommitOid, content.Content.TreeOid), token).ConfigureAwait(false);
        if (result.State is null) return Project(result);
        state = result.State;
        if (state.Stage != GitHubCoordinationStage.ProposalRefAdvanced) return Failure(GitHubPublicationRemoteFailureKind.Stale);
        createRight = result.PullRequestEntitlement;
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read);
        var checkedGit = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (checkedGit.Inspection is null) return Project(checkedGit);
        var pr = await prs.ObserveVerifiedAsync(read.State, checkedGit.Inspection, previous, token: token).ConfigureAwait(false);
        return await FinishProposal(read.State, checkedGit.Inspection, pr, payload, allowCreate: false, token).ConfigureAwait(false);
    }

    private async ValueTask<GitHubPublicationResult> FinishProposal(IGitHubCoordinationStateCapability state,
        IGitHubInspectedProposal proof, PrResult observed, ValidatedGitHubChangedFilePayload payload, bool allowCreate,
        CancellationToken token)
    {
        if (observed.Observation is not null) return await Complete(state, observed.Observation, payload, token).ConfigureAwait(false);
        if (observed.Outcome != PrOutcome.Absent) return Project(observed);
        if (!allowCreate)
            return GitHubPublicationResult.RecoveredRefPartial(new(proof.Ref, proof.BeforeOid, proof.ObservedRefOid,
                Authority.OperationCommitmentSha256));
        if (Authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        var right = createRight;
        createRight = null;
        var result = right is null
            ? await prs.RecoverAsync(state, new(proof.Ref, proof.CommitOid, proof.TreeOid), previous, token).ConfigureAwait(false)
            : await prs.CreateAuthorizedAsync(state, new(proof.Ref, proof.CommitOid, proof.TreeOid), right, token).ConfigureAwait(false);
        return result.Observation is null ? Project(result)
            : await Complete(state, result.Observation, payload, token).ConfigureAwait(false);
    }

    private async ValueTask<GitHubPublicationResult> Complete(IGitHubCoordinationStateCapability state,
        IGitHubProposalObservation observation, ValidatedGitHubChangedFilePayload payload, CancellationToken token,
        int remainingObservations = 3)
    {
        previous = observation;
        // Partial append holds concern the already authenticated preceding PR, not unpublished candidate Q.
        if (state.ProposalCommitOid is null) return Lifecycle(observation);
        var desired = observation.Outcome switch
        {
            PrOutcome.StaleDraft => GitHubCoordinationStage.StaleDraft,
            PrOutcome.Ready or PrOutcome.HeldDraft => GitHubCoordinationStage.AwaitingReview,
            PrOutcome.Merged => GitHubCoordinationStage.Merged,
            PrOutcome.ClosedUnmerged => GitHubCoordinationStage.ClosedUnmerged,
            _ => GitHubCoordinationStage.Published,
        };
        var replay = state.Stage == desired;
        if (!replay)
        {
            var recorded = await coordination.RecordObservationAsync(observation, token).ConfigureAwait(false);
            if (recorded.State is null) return Project(recorded);
            state = recorded.State;
        }
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read);
        if (read.State.HeadOid != state.HeadOid) return Failure(GitHubPublicationRemoteFailureKind.Conflict);
        var resources = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (resources.Inspection is null) return Project(resources);
        var confirmed = await prs.ObserveVerifiedAsync(read.State, resources.Inspection, observation, token: token).ConfigureAwait(false);
        if (confirmed.Observation is null) return Project(confirmed);
        previous = confirmed.Observation;
        if (confirmed.Outcome != observation.Outcome)
            return remainingObservations > 0
                ? await Complete(read.State, confirmed.Observation, payload, token, remainingObservations - 1).ConfigureAwait(false)
                : Failure(GitHubPublicationRemoteFailureKind.Conflict);
        if (desired == GitHubCoordinationStage.Published)
            return replay ? GitHubPublicationResult.ReplayNoOp(new(GitHubPublicationFactory.CreateCoordinationRef(Authority),
                    state.HeadOid, state.OperationId, state.OperationCommitmentSha256))
                : GitHubPublicationResult.Published(Identity(observation));
        return Lifecycle(observation);
    }

    private bool SameOperation(IGitHubCoordinationStateCapability state) =>
        state.OperationCommitmentSha256 == Authority.OperationCommitmentSha256
        && state.AuthorityCommitmentSha256 == Authority.AuthorityCommitmentSha256;

    private bool AppendIdentityMatches(IGitHubCoordinationStateCapability state) =>
        Authority.PrecedingOperationId == state.OperationId && Authority.PrecedingAuthorityCommitmentSha256 == state.AuthorityCommitmentSha256
        && Authority.PrecedingCandidateCommitmentSha256 == state.CurrentCandidateCommitmentSha256
        && Authority.GenerationId == state.GenerationId && Authority.SnapshotCommitmentSha256 == state.SnapshotCommitmentSha256
        && Authority.ExpectedBaseCommitOid == state.TargetCommitOid
        && Authority.PrecedingChangedFiles.Length == state.CumulativeChangedFiles.Length
        && Authority.PrecedingChangedFiles.Zip(state.CumulativeChangedFiles)
            .All(p => p.First.Path == p.Second.Path && p.First.CandidateFileSha256 == p.Second.CandidateSha256);

    private bool SuccessorMatches(IGitHubCoordinationStateCapability state, IGitHubProposalObservation observation) =>
        Authority.TerminalPredecessor is { } expected && expected.PullRequestNumber == observation.PullRequest.Number
        && expected.GenerationId == state.GenerationId && expected.HeadOid == observation.PullRequest.Head.Oid
        && Authority.GenerationId != state.GenerationId && Authority.SnapshotCommitmentSha256 != state.SnapshotCommitmentSha256
        && ((Authority.Transition == GitHubPublicationTransitionKind.SuccessorAfterMerge && observation.Outcome == PrOutcome.Merged)
            || (Authority.Transition == GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged
                && observation.Outcome == PrOutcome.ClosedUnmerged && Authority.ClosedUnmergedSuccessorAuthorization is not null));

    private bool Exhausted(IGitHubCoordinationStateCapability state) =>
        state.CumulativeDocumentationBlocks >= Authority.Policy.MaximumDocumentationBlocks
        || state.CumulativeChangedFiles.Length >= Authority.Policy.MaximumDistinctChangedFiles
        || state.CumulativePatchBytes >= Authority.Policy.MaximumCumulativePatchBytes;

    private static GitHubPublicationPullRequestIdentity Identity(IGitHubProposalObservation observation) =>
        new(observation.PullRequest.Number, observation.Coordination.GenerationId,
            "refs/heads/" + observation.PullRequest.Head.Ref, observation.PullRequest.Head.Oid!,
            observation.Coordination.OperationCommitmentSha256);
    private static GitHubPublicationResult Hold(IGitHubProposalObservation observation) => GitHubPublicationResult.AwaitingReview(Identity(observation));
    private static GitHubPublicationResult StaleDraft(IGitHubProposalObservation observation) =>
        GitHubPublicationResult.StaleBaseAfterCreate(new(observation.PullRequest.Number, observation.Metadata.MarkerHash,
            "refs/heads/" + observation.PullRequest.Head.Ref, observation.PullRequest.Head.Oid!, observation.Coordination.TargetRef,
            observation.Coordination.TargetCommitOid, observation.PullRequest.BaseOid, observation.Coordination.GenerationId,
            observation.Coordination.OperationId, observation.Coordination.OperationCommitmentSha256));
    private static GitHubPublicationResult Lifecycle(IGitHubProposalObservation observation) => observation.Outcome switch
    {
        PrOutcome.Ready or PrOutcome.HeldDraft => Hold(observation),
        PrOutcome.Merged => GitHubPublicationResult.Merged(Identity(observation)),
        PrOutcome.ClosedUnmerged => GitHubPublicationResult.ClosedUnmerged(Identity(observation)),
        PrOutcome.StaleDraft => StaleDraft(observation),
        _ => Failure(GitHubPublicationRemoteFailureKind.Conflict),
    };
    private static GitHubPublicationResult Failure(GitHubPublicationRemoteFailureKind kind) => GitHubPublicationResult.FromRemoteFailure(kind);
    private static GitHubPublicationResult Transport(GitHubFailure? failure) => Failure(failure?.Code switch
    {
        GitHubFailureCode.Authentication or GitHubFailureCode.Permission => GitHubPublicationRemoteFailureKind.Permission,
        GitHubFailureCode.RateLimit => GitHubPublicationRemoteFailureKind.RateLimit,
        GitHubFailureCode.Cancelled => GitHubPublicationRemoteFailureKind.Cancelled,
        GitHubFailureCode.Timeout => GitHubPublicationRemoteFailureKind.Timeout,
        GitHubFailureCode.Conflict or GitHubFailureCode.NotFound => GitHubPublicationRemoteFailureKind.Conflict,
        _ => GitHubPublicationRemoteFailureKind.HostFailure,
    });
    private static GitHubPublicationResult Domain(GitHubCoordinationFailureKind? cause) => Failure(cause switch
    {
        GitHubCoordinationFailureKind.TargetMoved => GitHubPublicationRemoteFailureKind.Stale,
        GitHubCoordinationFailureKind.HumanChange or GitHubCoordinationFailureKind.ObjectMismatch => GitHubPublicationRemoteFailureKind.HumanChange,
        _ => GitHubPublicationRemoteFailureKind.Conflict,
    });
    private static GitHubPublicationResult Project(GitHubCoordinationResult result) =>
        (result.Failure?.ReadbackFailure ?? result.Failure?.TransportFailure) is { } error
            ? Transport(error) : Domain(result.Failure?.Kind);
    private static GitHubPublicationResult Project(PrResult result) =>
        result.Failure is { } error ? Transport(error) : Domain(result.Cause);
    private static GitHubPublicationResult Project(GitResult result) =>
        (result.Failure?.Readback ?? result.Failure?.Transport) is { } error ? Transport(error)
            : result.Failure?.CoordinationCause is { } cause ? Domain(cause)
            : Failure(result.Failure?.Kind == GitHubProposalFailureKind.Integrity
                ? GitHubPublicationRemoteFailureKind.HumanChange : GitHubPublicationRemoteFailureKind.Conflict);
}
