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
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default) =>
        (await PublishObservedAsync(authority, payload, cancellationToken).ConfigureAwait(false)).Result;

    internal async ValueTask<GitHubPublicationAttempt> PublishObservedAsync(ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default)
    {
        if (!Correlates(authority, payload))
            return new(GitHubPublicationResult.LocalInvalid(GitHubPublicationValidationCode.InvalidCorrelation,
                GitHubPublicationFieldId.Payload), new(GitHubPublicationBoundary.Reconcile,
                    GitHubPublicationOwner.Reconciler, Predicate: GitHubPublicationPredicate.InvalidCorrelation));
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Reconcile(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Reject(GitHubPublicationRemoteFailureKind.Cancelled, GitHubPublicationPredicate.Cancelled); }
        catch { return Reject(GitHubPublicationRemoteFailureKind.HostFailure, GitHubPublicationPredicate.UnhandledException); }
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

    private async ValueTask<GitHubPublicationAttempt> Reconcile(ValidatedGitHubChangedFilePayload payload, CancellationToken token)
    {
        var repository = await client.GetRepositoryAsync(token).ConfigureAwait(false);
        if (repository.Value is null) return new(Transport(repository.Failure),
            new(GitHubPublicationBoundary.Repository, GitHubPublicationOwner.Transport,
                TransportCode: repository.Failure?.Code, TransportHttpStatus: repository.Failure?.HttpStatus,
                Delivery: repository.Delivery));
        if (repository.Value.Archived || repository.Value.Disabled) return Reject(GitHubPublicationRemoteFailureKind.Permission, GitHubPublicationPredicate.RepositoryUnavailable);
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null) return Project(read, GitHubPublicationBoundary.CoordinationRead);
        var state = read.State;
        if (state is not null && !SameOperation(state))
        {
            if (state.OperationId == Authority.OperationId
                || state.Stage is GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated)
                return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.DifferentOperation);
            var old = await git.InspectPredecessorAsync(state, token).ConfigureAwait(false);
            if (old.Inspection is null) return Project(old, GitHubPublicationBoundary.GitInspectPredecessor);
            var observed = old.Inspection.ContentExists && state.ProposalCommitOid is not null
                ? await prs.ObserveVerifiedAsync(state, old.Inspection, previous, token: token).ConfigureAwait(false)
                : await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
            if (observed.Outcome is PrOutcome.Failed or PrOutcome.Conflict or PrOutcome.Unresolved) return Project(observed, old.Inspection.ContentExists && state.ProposalCommitOid is not null ? GitHubPublicationBoundary.PullRequestObserve : GitHubPublicationBoundary.PullRequestPreflight);
            previous = observed.Observation;
            if (observed.Outcome == PrOutcome.StaleDraft) return StaleDraft(observed.Observation!);
            if (observed.Outcome is PrOutcome.Ready or PrOutcome.HeldDraft) return Hold(observed.Observation!);
            if (observed.Outcome is PrOutcome.Merged or PrOutcome.ClosedUnmerged)
            {
                if (!SuccessorMatches(state, observed.Observation!)) return Lifecycle(observed.Observation!);
                if (coordination.Inspect(read.Read)!.TargetOid != Authority.ExpectedBaseCommitOid)
                    return Reject(GitHubPublicationRemoteFailureKind.Stale, GitHubPublicationPredicate.TargetMoved);
                var terminal = observed.Outcome == PrOutcome.Merged ? GitHubCoordinationStage.Merged : GitHubCoordinationStage.ClosedUnmerged;
                if (state.Stage != terminal)
                {
                    var recorded = await coordination.RecordObservationAsync(observed.Observation!, token).ConfigureAwait(false);
                    if (recorded.State is null) return Project(recorded, GitHubPublicationBoundary.CoordinationRecord);
                    read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
                    if (read.Read is null || read.State is null) return Project(read, GitHubPublicationBoundary.CoordinationRead);
                    state = read.State;
                    old = await git.InspectPredecessorAsync(state, token).ConfigureAwait(false);
                    if (old.Inspection is null) return Project(old, GitHubPublicationBoundary.GitInspectPredecessor);
                    observed = await prs.ObserveVerifiedAsync(state, old.Inspection, previous, token: token).ConfigureAwait(false);
                    if (observed.Observation is null) return Project(observed, GitHubPublicationBoundary.PullRequestObserve);
                    if (!SuccessorMatches(state, observed.Observation)) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.SuccessorMismatch);
                }
            }
            else if (Authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
            {
                if (observed.Outcome != PrOutcome.Appendable || state.Stage != GitHubCoordinationStage.Published
                    || !AppendIdentityMatches(state)) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.AppendMismatch);
                if (state.PolicyCommitmentSha256 != Authority.PolicyCommitmentSha256 || Exhausted(state))
                    return Hold(observed.Observation!);
            }
            else if (!(Authority.Transition == GitHubPublicationTransitionKind.Initial
                && state.Stage == GitHubCoordinationStage.Stale && Authority.GenerationId != state.GenerationId
                && observed.Outcome == PrOutcome.Absent)) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.TransitionMismatch);
        }

        if (state is null || !SameOperation(state))
        {
            var preflight = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
            if (preflight.Inspection is null) return Project(preflight, GitHubPublicationBoundary.GitInspect);
            if (Authority.Transition != GitHubPublicationTransitionKind.SameSnapshotAppend)
            {
                if (preflight.Inspection.ObservedRefOid != GitHubPublicationContract.MissingGitObjectId)
                    return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.UnexpectedProposalRef);
                var campaign = await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
                if (campaign.Outcome != PrOutcome.Absent) return Project(campaign, GitHubPublicationBoundary.PullRequestPreflight);
                previous = null;
            }
            var claim = await coordination.ClaimAsync(read.Read, token).ConfigureAwait(false);
            if (claim.State is null) return Project(claim, GitHubPublicationBoundary.CoordinationClaim);
            state = claim.State;
            if (!SameOperation(state)) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.ClaimMismatch);
        }

        // Discover actual content/ref/PR residuals before any stale-stage mutation.
        read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read, GitHubPublicationBoundary.CoordinationRead);
        state = read.State;
        if (!SameOperation(state)) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.CurrentMismatch);
        var inspected = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (inspected.Inspection is null) return Project(inspected, GitHubPublicationBoundary.GitInspect);
        var proof = inspected.Inspection;
        if (state.Stage == GitHubCoordinationStage.Claimed && proof.ObservedRefOid == proof.CommitOid)
            return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.ClaimedRefPresent);
        var hasPrContext = state.ProposalCommitOid is not null || state.Transition == "same-snapshot-append";
        var pr = hasPrContext
            ? await prs.ObserveVerifiedAsync(state, proof, previous, token: token).ConfigureAwait(false)
            : await prs.PreflightAsync(read.Read, token).ConfigureAwait(false);
        if (pr.Outcome is PrOutcome.Failed or PrOutcome.Conflict or PrOutcome.Unresolved) return Project(pr, hasPrContext ? GitHubPublicationBoundary.PullRequestObserve : GitHubPublicationBoundary.PullRequestPreflight);
        previous = pr.Observation ?? previous;
        if (pr.Observation is not null && pr.Outcome is not PrOutcome.Appendable)
            return await Complete(state, pr.Observation, payload, token).ConfigureAwait(false);
        if (state.Stage is GitHubCoordinationStage.Stale or GitHubCoordinationStage.StaleDraft)
            return Reject(GitHubPublicationRemoteFailureKind.Stale, GitHubPublicationPredicate.StaleStage);
        if (coordination.Inspect(read.Read)!.TargetOid != Authority.ExpectedBaseCommitOid)
        {
            var update = GitHubCoordinationStageUpdate.Stale(Authority.ExpectedBaseCommitOid, coordination.Inspect(read.Read)!.TargetOid);
            if (proof.ContentExists && state.Stage is GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated)
            {
                if (state.Stage == GitHubCoordinationStage.Claimed) update = GitHubCoordinationStageUpdate.ContentCreated(proof.CommitOid);
                else if (proof.ObservedRefOid == proof.CommitOid) update = GitHubCoordinationStageUpdate.ProposalRefAdvanced(proof.CommitOid, proof.TreeOid);
            }
            var stale = await coordination.AdvanceAsync(state, update, token).ConfigureAwait(false);
            if (stale.State is null) return Project(stale, update.Stage switch
            {
                GitHubCoordinationStage.ContentCreated => GitHubPublicationBoundary.CoordinationAdvanceContent,
                GitHubCoordinationStage.ProposalRefAdvanced => GitHubPublicationBoundary.CoordinationAdvanceRef,
                _ => GitHubPublicationBoundary.CoordinationAdvanceStale,
            });
            return Reject(GitHubPublicationRemoteFailureKind.Stale, GitHubPublicationPredicate.TargetMoved);
        }
        return state.Stage switch
        {
            GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated =>
                await AdvanceProposal(state, payload, token).ConfigureAwait(false),
            GitHubCoordinationStage.ProposalRefAdvanced =>
                await FinishProposal(state, proof, pr, payload, allowCreate: true, token).ConfigureAwait(false),
            GitHubCoordinationStage.PullRequestCreated or GitHubCoordinationStage.Published or GitHubCoordinationStage.AwaitingReview
                when pr.Observation is not null => await Complete(state, pr.Observation, payload, token).ConfigureAwait(false),
            _ => Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.UnexpectedStage),
        };
    }

    private async ValueTask<GitHubPublicationAttempt> AdvanceProposal(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, CancellationToken token)
    {
        var prepared = await git.PrepareAsync(state, payload, token).ConfigureAwait(false);
        if (prepared.Prepared is null) return Project(prepared, GitHubPublicationBoundary.GitPrepare);
        var content = await git.CreateContentAsync(prepared.Prepared, state, token).ConfigureAwait(false);
        if (content.Failure is not null || content.Content is null) return Project(content, GitHubPublicationBoundary.GitCreateContent);
        IGitHubProposalRefEntitlement? right = null;
        if (state.Stage == GitHubCoordinationStage.Claimed)
        {
            var recorded = await coordination.AdvanceAsync(state,
                GitHubCoordinationStageUpdate.ContentCreated(content.Content.CommitOid), token).ConfigureAwait(false);
            if (recorded.State is null) return Project(recorded, GitHubPublicationBoundary.CoordinationAdvanceContent);
            state = recorded.State;
            if (state.Stage == GitHubCoordinationStage.Stale) return Reject(GitHubPublicationRemoteFailureKind.Stale, GitHubPublicationPredicate.StaleStage);
            right = recorded.ProposalRefEntitlement;
        }
        // The active PR may have changed while immutable objects were materialized.
        if (state.Transition == "same-snapshot-append")
        {
            var current = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
            if (current.Read is null) return Project(current, GitHubPublicationBoundary.CoordinationRead);
            var inspected = await git.InspectAsync(current.Read, payload, token).ConfigureAwait(false);
            if (inspected.Inspection is null) return Project(inspected, GitHubPublicationBoundary.GitInspect);
            var observed = await prs.ObserveVerifiedAsync(state, inspected.Inspection, previous, token: token).ConfigureAwait(false);
            if (observed.Observation is null) return Project(observed, GitHubPublicationBoundary.PullRequestObserve);
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
            return Project(advanced, GitHubPublicationBoundary.GitAdvanceRef);
        }
        var result = await coordination.AdvanceAsync(state,
            GitHubCoordinationStageUpdate.ProposalRefAdvanced(content.Content.CommitOid, content.Content.TreeOid), token).ConfigureAwait(false);
        if (result.State is null) return Project(result, GitHubPublicationBoundary.CoordinationAdvanceRef);
        state = result.State;
        if (state.Stage != GitHubCoordinationStage.ProposalRefAdvanced) return Reject(GitHubPublicationRemoteFailureKind.Stale, GitHubPublicationPredicate.UnexpectedStage);
        createRight = result.PullRequestEntitlement;
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read, GitHubPublicationBoundary.CoordinationRead);
        var checkedGit = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (checkedGit.Inspection is null) return Project(checkedGit, GitHubPublicationBoundary.GitInspect);
        var pr = await prs.ObserveVerifiedAsync(read.State, checkedGit.Inspection, previous, token: token).ConfigureAwait(false);
        return await FinishProposal(read.State, checkedGit.Inspection, pr, payload, allowCreate: false, token).ConfigureAwait(false);
    }

    private async ValueTask<GitHubPublicationAttempt> FinishProposal(IGitHubCoordinationStateCapability state,
        IGitHubInspectedProposal proof, PrResult observed, ValidatedGitHubChangedFilePayload payload, bool allowCreate,
        CancellationToken token)
    {
        if (observed.Observation is not null) return await Complete(state, observed.Observation, payload, token).ConfigureAwait(false);
        if (observed.Outcome != PrOutcome.Absent) return Project(observed, GitHubPublicationBoundary.PullRequestObserve);
        if (!allowCreate)
            return GitHubPublicationResult.RecoveredRefPartial(new(proof.Ref, proof.BeforeOid, proof.ObservedRefOid,
                Authority.OperationCommitmentSha256));
        if (Authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.AppendCreateForbidden);
        var right = createRight;
        createRight = null;
        var result = right is null
            ? await prs.RecoverAsync(state, new(proof.Ref, proof.CommitOid, proof.TreeOid), previous, token).ConfigureAwait(false)
            : await prs.CreateAuthorizedAsync(state, new(proof.Ref, proof.CommitOid, proof.TreeOid), right, token).ConfigureAwait(false);
        return result.Observation is null ? Project(result, right is null ? GitHubPublicationBoundary.PullRequestRecover : GitHubPublicationBoundary.PullRequestCreate)
            : await Complete(state, result.Observation, payload, token).ConfigureAwait(false);
    }

    private async ValueTask<GitHubPublicationAttempt> Complete(IGitHubCoordinationStateCapability state,
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
            if (recorded.State is null) return Project(recorded, GitHubPublicationBoundary.CoordinationRecord);
            state = recorded.State;
        }
        var read = await coordination.ReadCurrentAsync(token).ConfigureAwait(false);
        if (read.Read is null || read.State is null) return Project(read, GitHubPublicationBoundary.CoordinationRead);
        if (read.State.HeadOid != state.HeadOid) return Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.CompletionHeadChanged);
        var resources = await git.InspectAsync(read.Read, payload, token).ConfigureAwait(false);
        if (resources.Inspection is null) return Project(resources, GitHubPublicationBoundary.GitInspect);
        var confirmed = await prs.ObserveVerifiedAsync(read.State, resources.Inspection, observation, token: token).ConfigureAwait(false);
        if (confirmed.Observation is null) return Project(confirmed, GitHubPublicationBoundary.PullRequestObserve);
        previous = confirmed.Observation;
        if (confirmed.Outcome != observation.Outcome)
            return remainingObservations > 0
                ? await Complete(read.State, confirmed.Observation, payload, token, remainingObservations - 1).ConfigureAwait(false)
                : Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.ObservationLimit);
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
    private static GitHubPublicationAttempt Lifecycle(IGitHubProposalObservation observation) => observation.Outcome switch
    {
        PrOutcome.Ready or PrOutcome.HeldDraft => Hold(observation),
        PrOutcome.Merged => GitHubPublicationResult.Merged(Identity(observation)),
        PrOutcome.ClosedUnmerged => GitHubPublicationResult.ClosedUnmerged(Identity(observation)),
        PrOutcome.StaleDraft => StaleDraft(observation),
        _ => Reject(GitHubPublicationRemoteFailureKind.Conflict, GitHubPublicationPredicate.LifecycleOutcome),
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
    private static GitHubPublicationAttempt Reject(GitHubPublicationRemoteFailureKind kind,
        GitHubPublicationPredicate predicate) => new(Failure(kind),
            new(GitHubPublicationBoundary.Reconcile, GitHubPublicationOwner.Reconciler, Predicate: predicate));

    private static GitHubPublicationAttempt Project(GitHubCoordinationResult result, GitHubPublicationBoundary boundary)
    {
        var failure = result.Failure;
        return new(Map(result), new(boundary, GitHubPublicationOwner.Coordination,
            CoordinationFailure: failure?.Kind, TransportCode: failure?.TransportFailure?.Code,
            TransportHttpStatus: failure?.TransportFailure?.HttpStatus, Delivery: failure?.Delivery,
            RecoveryCode: failure?.ReadbackFailure?.Code, RecoveryHttpStatus: failure?.ReadbackFailure?.HttpStatus,
            ObjectKind: (failure?.Context as GitHubObjectContext)?.Kind,
            Predicate: failure is null ? GitHubPublicationPredicate.MissingOrUnexpectedComponent : null));
    }

    private static GitHubPublicationAttempt Project(PrResult result, GitHubPublicationBoundary boundary) =>
        new(Map(result), new(boundary, GitHubPublicationOwner.PullRequests,
            CoordinationFailure: result.Cause, PullRequestOutcome: result.Outcome,
            TransportCode: result.Failure?.Code, TransportHttpStatus: result.Failure?.HttpStatus,
            Delivery: result.Delivery,
            Predicate: result.Failure is null && result.Cause is null
                ? GitHubPublicationPredicate.MissingOrUnexpectedComponent : null));

    private static GitHubPublicationAttempt Project(GitResult result, GitHubPublicationBoundary boundary)
    {
        var failure = result.Failure;
        return new(Map(result), new(boundary, GitHubPublicationOwner.GitData,
            CoordinationFailure: failure?.CoordinationCause, ProposalFailure: failure?.Kind,
            TransportCode: failure?.Transport?.Code, TransportHttpStatus: failure?.Transport?.HttpStatus,
            Delivery: failure?.Delivery, RecoveryCode: failure?.Readback?.Code,
            RecoveryHttpStatus: failure?.Readback?.HttpStatus, ObjectKind: (failure?.Context as GitHubObjectContext)?.Kind,
            Predicate: failure is null ? GitHubPublicationPredicate.MissingOrUnexpectedComponent : null));
    }

    // An empty lookup is recovery evidence, not a replacement diagnosis. A failed recovery
    // read wins; a verified domain rejection wins over a reconciled mutation response loss.
    private static GitHubFailure? RecoveryError(GitHubFailure? recovery, GitHubFailure? mutation) =>
        recovery?.Code == GitHubFailureCode.NotFound ? mutation ?? recovery : recovery ?? mutation;
    private static GitHubPublicationResult Map(GitHubCoordinationResult result)
    {
        var failure = result.Failure;
        if (failure?.ReadbackFailure is { Code: not GitHubFailureCode.NotFound } read)
            return Transport(read);
        if (failure is { ReadbackFailure: null, Kind: not (GitHubCoordinationFailureKind.Transport or GitHubCoordinationFailureKind.Unresolved) })
            return Domain(failure.Kind);
        return RecoveryError(failure?.ReadbackFailure, failure?.TransportFailure) is { } error
            ? Transport(error) : Domain(failure?.Kind);
    }
    private static GitHubPublicationResult Map(PrResult result) =>
        result.Failure is { } error ? Transport(error) : Domain(result.Cause);
    private static GitHubPublicationResult Map(GitResult result)
    {
        var failure = result.Failure;
        if (failure?.Readback is { Code: not GitHubFailureCode.NotFound } read
            && !(failure.Kind == GitHubProposalFailureKind.Integrity && read.Code == GitHubFailureCode.InvalidResponse))
            return Transport(read);
        if (failure?.CoordinationCause is { } cause
            && cause is not (GitHubCoordinationFailureKind.Transport or GitHubCoordinationFailureKind.Unresolved)
            && failure.Readback is null) return Domain(cause);
        if (failure?.Kind == GitHubProposalFailureKind.Integrity)
            return Failure(GitHubPublicationRemoteFailureKind.HumanChange);
        return RecoveryError(failure?.Readback, failure?.Transport) is { } error ? Transport(error)
            : Domain(failure?.CoordinationCause);
    }
}
