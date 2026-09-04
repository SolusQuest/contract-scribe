using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal enum GitHubCoordinationOutcome
{
    ExpectedAbsence,
    Current,
    Admitted,
    Replayed,
    Advanced,
    Stale,
    Guarded,
    Conflict,
    Failed,
}

internal enum GitHubCoordinationFailureKind
{
    InvalidInput,
    MissingPredecessor,
    DifferentOperation,
    StageConflict,
    TargetMoved,
    HumanChange,
    Conflict,
    ObjectMismatch,
    Bounds,
    Unresolved,
    Transport,
}

internal interface IGitHubCoordinationReadCapability;

internal interface IGitHubCoordinationStateCapability
{
    GitHubRepositoryIdentity Repository { get; }
    GitHubCoordinationStage Stage { get; }
    string HeadOid { get; }
    string TargetRef { get; }
    string TargetCommitOid { get; }
    string SnapshotCommitmentSha256 { get; }
    string AuthorityCommitmentSha256 { get; }
    string PolicyCommitmentSha256 { get; }
    string OperationId { get; }
    string OperationCommitmentSha256 { get; }
    string CurrentCandidateCommitmentSha256 { get; }
    string? PrecedingOperationId { get; }
    string? PrecedingAuthorityCommitmentSha256 { get; }
    string? PrecedingCandidateCommitmentSha256 { get; }
    string GenerationId { get; }
    string Transition { get; }
    string CoordinationPredecessorOid { get; }
    string? ContentCommitOid { get; }
    string? ProposalRefOid { get; }
    string? ProposalCommitOid { get; }
    string? ProposalTreeOid { get; }
    string? PullRequestCreationOperationCommitmentSha256 { get; }
    int? PullRequestNumber { get; }
    string? ExpectedBaseOid { get; }
    string? ObservedBaseOid { get; }
    string? OwnershipMarkerSha256 { get; }
    int CumulativeDocumentationBlocks { get; }
    long CumulativePatchBytes { get; }
    ImmutableArray<GitHubCoordinationChangedFile> CumulativeChangedFiles { get; }
}

internal interface IGitHubCoordinationGuardCapability
{
    IGitHubCoordinationStateCapability State { get; }
}

internal sealed class GitHubCoordinationFailure
{
    internal GitHubCoordinationFailure(
        GitHubCoordinationFailureKind kind,
        GitHubFailure? transportFailure = null,
        GitHubDelivery delivery = GitHubDelivery.NotDispatched,
        GitHubMutationContext? context = null,
        GitHubPermissionAlternatives? permissions = null,
        GitHubFailure? readbackFailure = null)
    {
        Kind = kind;
        TransportFailure = transportFailure;
        Delivery = delivery;
        Context = context;
        Permissions = permissions;
        ReadbackFailure = readbackFailure;
    }

    internal GitHubCoordinationFailureKind Kind { get; }
    internal GitHubFailure? TransportFailure { get; }
    internal GitHubDelivery Delivery { get; }
    internal GitHubMutationContext? Context { get; }
    internal GitHubPermissionAlternatives? Permissions { get; }
    internal GitHubFailure? ReadbackFailure { get; }
    public override string ToString() => nameof(GitHubCoordinationFailure);
}

internal sealed class GitHubCoordinationResult
{
    internal GitHubCoordinationResult(
        GitHubCoordinationOutcome outcome,
        IGitHubCoordinationReadCapability? read = null,
        IGitHubCoordinationStateCapability? state = null,
        IGitHubCoordinationGuardCapability? guard = null,
        GitHubCoordinationFailure? failure = null)
    {
        Outcome = outcome;
        Read = read;
        State = state;
        Guard = guard;
        Failure = failure;
    }

    internal GitHubCoordinationOutcome Outcome { get; }
    internal IGitHubCoordinationReadCapability? Read { get; }
    internal IGitHubCoordinationStateCapability? State { get; }
    internal IGitHubCoordinationGuardCapability? Guard { get; }
    internal GitHubCoordinationFailure? Failure { get; }
    public override string ToString() => nameof(GitHubCoordinationResult);
}

internal sealed class GitHubCoordinationStore
{
    private static readonly string ZeroOid = GitHubPublicationContract.MissingGitObjectId;
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumSameOperationTransitions = 6;
    private readonly GitHubApiClient client;
    private readonly ValidatedGitHubPublicationAuthority authority;
    private readonly string coordinationRef;

    private GitHubCoordinationStore(GitHubApiClient client)
    {
        this.client = client;
        authority = client.Authority;
        coordinationRef = GitHubPublicationFactory.CreateCoordinationRef(authority);
    }

    internal static GitHubCoordinationStore Create(GitHubApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client);
    }

    internal async ValueTask<GitHubCoordinationResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var repository = await client.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            if (repository.Value is null) return Failed(repository);
            var target = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
            if (target.Value is null) return Failed(target);
            var current = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
            if (current.Value is null)
            {
                if (current.Failure?.Code == GitHubFailureCode.NotFound
                    && authority.Transition == GitHubPublicationTransitionKind.Initial
                    && target.Value.Oid == authority.ExpectedBaseCommitOid)
                {
                    return new(GitHubCoordinationOutcome.ExpectedAbsence,
                        read: new ReadCapability(this, repository.Value.Identity, target.Value, null));
                }
                return Failed(current, current.Failure?.Code == GitHubFailureCode.NotFound
                    ? GitHubCoordinationFailureKind.MissingPredecessor
                    : GitHubCoordinationFailureKind.Transport);
            }
            var state = await ReadStateAsync(repository.Value.Identity, target.Value,
                current.Value.Oid, cancellationToken).ConfigureAwait(false);
            if (state.State is null) return state;
            var stateCapability = (StateCapability)state.State;
            return new(GitHubCoordinationOutcome.Current,
                read: new ReadCapability(this, repository.Value.Identity, target.Value, stateCapability),
                state: stateCapability);
        }
        catch (GitHubCoordinationException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        }
        catch (OperationCanceledException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.Cancelled));
        }
        catch
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.HostFailure));
        }
    }

    internal async ValueTask<GitHubCoordinationResult> ClaimAsync(
        IGitHubCoordinationReadCapability read,
        CancellationToken cancellationToken = default)
    {
        if (read is not ReadCapability owned || !ReferenceEquals(owned.Owner, this))
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        try
        {
            var currentRef = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
            var observedCurrent = owned.Current;
            if (currentRef.Value is not null
                && (observedCurrent is null || currentRef.Value.Oid != observedCurrent.HeadOid))
            {
                var expectedPredecessor = observedCurrent?.HeadOid ?? ZeroOid;
                var expectedClaim = GitHubCoordinationObjects.Prepare(
                    GitHubCoordinationCodec.CreateClaim(authority, expectedPredecessor));
                if (currentRef.Value.Oid != expectedClaim.CommitOid)
                    return DomainFailure(GitHubCoordinationFailureKind.Conflict);
                var moved = await ReadStateAsync(owned.Repository, owned.Target,
                    currentRef.Value.Oid, cancellationToken).ConfigureAwait(false);
                if (moved.State is null) return moved;
                var movedState = (StateCapability)moved.State;
                if (movedState.State.OperationId != authority.OperationId
                    || !MatchesAuthority(movedState.State))
                    return DomainFailure(GitHubCoordinationFailureKind.Conflict);
                observedCurrent = movedState;
            }
            else if (currentRef.Value is null)
            {
                if (observedCurrent is not null || currentRef.Failure?.Code != GitHubFailureCode.NotFound)
                    return Failed(currentRef);
            }

            if (observedCurrent is not null
                && observedCurrent.State.OperationId == authority.OperationId)
            {
                if (!MatchesAuthority(observedCurrent.State))
                    return DomainFailure(GitHubCoordinationFailureKind.DifferentOperation);
                var target = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
                if (target.Value is null) return Failed(target);
                if (target.Value.Oid != authority.ExpectedBaseCommitOid
                    && CanBecomeStale(observedCurrent.State.Stage))
                    return await AdvanceCoreAsync(observedCurrent,
                        GitHubCoordinationStageUpdate.Stale(authority.ExpectedBaseCommitOid, target.Value.Oid),
                        cancellationToken, checkTarget: false).ConfigureAwait(false);
                return new(GitHubCoordinationOutcome.Replayed, state: observedCurrent);
            }

            var predecessor = observedCurrent?.HeadOid ?? ZeroOid;
            var claim = GitHubCoordinationCodec.CreateClaim(authority, predecessor);
            if (!ValidAuthorityRoot(claim, observedCurrent?.State))
                return DomainFailure(GitHubCoordinationFailureKind.StageConflict);
            var prepared = GitHubCoordinationObjects.Prepare(claim);
            var orphan = await client.GetCommitAsync(prepared.CommitOid, cancellationToken).ConfigureAwait(false);
            if (orphan.Value is not null)
                return DomainFailure(GitHubCoordinationFailureKind.Unresolved);
            if (orphan.Failure?.Code != GitHubFailureCode.NotFound) return Failed(orphan);

            var objects = await CreateObjectsAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (objects is not null) return objects;

            var finalTarget = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
            if (finalTarget.Value is null) return Failed(finalTarget);
            if (finalTarget.Value.Oid != authority.ExpectedBaseCommitOid)
                return DomainFailure(GitHubCoordinationFailureKind.TargetMoved);

            var finalCurrent = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
            if (observedCurrent is null)
            {
                if (finalCurrent.Value is not null || finalCurrent.Failure?.Code != GitHubFailureCode.NotFound)
                    return finalCurrent.Value is not null
                        ? DomainFailure(GitHubCoordinationFailureKind.Conflict)
                        : Failed(finalCurrent);
            }
            else if (finalCurrent.Value?.Oid != observedCurrent.HeadOid)
                return finalCurrent.Value is not null
                    ? DomainFailure(GitHubCoordinationFailureKind.Conflict)
                    : Failed(finalCurrent);

            var admitted = await UpdateAndReadAsync(owned.Repository, finalTarget.Value,
                prepared, predecessor, cancellationToken).ConfigureAwait(false);
            if (admitted.State is null) return admitted;
            var admittedState = (StateCapability)admitted.State;

            using var postRecovery = cancellationToken.IsCancellationRequested
                ? new CancellationTokenSource(RecoveryTimeout)
                : null;
            var postTarget = await client.GetRefAsync(authority.TargetRef,
                postRecovery?.Token ?? cancellationToken).ConfigureAwait(false);
            if (postTarget.Value is null)
                return postRecovery is null ? Failed(postTarget)
                    : DomainFailure(GitHubCoordinationFailureKind.Transport,
                        RecoveryFailure(postTarget.Failure, postRecovery), postTarget.Delivery,
                        postTarget.Context, postTarget.RequiredPermissions);
            if (postTarget.Value.Oid != authority.ExpectedBaseCommitOid)
            {
                if (cancellationToken.IsCancellationRequested)
                    return DomainFailure(GitHubCoordinationFailureKind.TargetMoved);
                return await AdvanceCoreAsync(admittedState,
                    GitHubCoordinationStageUpdate.Stale(authority.ExpectedBaseCommitOid, postTarget.Value.Oid),
                    cancellationToken, checkTarget: false).ConfigureAwait(false);
            }
            return new(GitHubCoordinationOutcome.Admitted, state: admittedState);
        }
        catch (GitHubCoordinationException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        }
        catch (OperationCanceledException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.Cancelled));
        }
        catch
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.HostFailure));
        }
    }

    internal ValueTask<GitHubCoordinationResult> AdvanceAsync(
        IGitHubCoordinationStateCapability current,
        GitHubCoordinationStageUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (current is not StateCapability owned || update is null
            || !ReferenceEquals(owned.Owner, this))
            return ValueTask.FromResult(DomainFailure(GitHubCoordinationFailureKind.InvalidInput));
        return AdvanceCoreAsync(owned, update, cancellationToken, checkTarget: true);
    }

    internal async ValueTask<GitHubCoordinationResult> ReadClaimAsync(
        IGitHubCoordinationStateCapability capability,
        CancellationToken cancellationToken = default)
    {
        if (capability is not StateCapability owned || !ReferenceEquals(owned.Owner, this)
            || !PermitsCreate(owned.State.Stage))
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        if (!MatchesAuthority(owned.State))
            return DomainFailure(GitHubCoordinationFailureKind.DifferentOperation);
        var repository = await client.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (repository.Value is null) return Failed(repository);
        if (repository.Value.Identity != owned.Repository)
            return DomainFailure(GitHubCoordinationFailureKind.HumanChange);
        var target = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
        if (target.Value is null) return Failed(target);
        if (target.Value.Oid != owned.State.TargetCommitOid)
            return DomainFailure(GitHubCoordinationFailureKind.TargetMoved);
        var current = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
        if (current.Value is null) return Failed(current);
        if (current.Value.Oid != owned.HeadOid)
            return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        var reread = await ReadStateAsync(repository.Value.Identity, target.Value,
            current.Value.Oid, cancellationToken).ConfigureAwait(false);
        if (reread.State is null) return reread;
        var rereadState = (StateCapability)reread.State;
        if (!SameState(owned, rereadState))
            return DomainFailure(GitHubCoordinationFailureKind.HumanChange);
        return new(GitHubCoordinationOutcome.Guarded, state: rereadState,
            guard: new GuardCapability(this, rereadState));
    }

    internal IGitHubCoordinationStateCapability? ValidateGuard(
        IGitHubCoordinationGuardCapability guard) =>
        guard is GuardCapability owned && ReferenceEquals(owned.Owner, this)
            ? owned.State
            : null;

    private async ValueTask<GitHubCoordinationResult> AdvanceCoreAsync(
        StateCapability current,
        GitHubCoordinationStageUpdate update,
        CancellationToken cancellationToken,
        bool checkTarget)
    {
        try
        {
            if (!MatchesAuthority(current.State))
                return DomainFailure(GitHubCoordinationFailureKind.DifferentOperation);
            if (!AllowsStage(current.State, update.Stage))
                return DomainFailure(GitHubCoordinationFailureKind.StageConflict);
            var repository = await client.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            if (repository.Value is null) return Failed(repository);
            if (repository.Value.Identity != current.Repository)
                return DomainFailure(GitHubCoordinationFailureKind.HumanChange);
            var refRead = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
            if (refRead.Value is null) return Failed(refRead);
            if (refRead.Value.Oid != current.HeadOid)
                return DomainFailure(GitHubCoordinationFailureKind.Conflict);
            var authenticated = await ReadStateAsync(repository.Value.Identity, current.Target,
                current.HeadOid, cancellationToken).ConfigureAwait(false);
            if (authenticated.State is null) return authenticated;
            if (!SameState(current, (StateCapability)authenticated.State))
                return DomainFailure(GitHubCoordinationFailureKind.HumanChange);

            var target = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
            if (target.Value is null) return Failed(target);
            var intended = Apply(current.State, update, current.HeadOid);
            var effective = update;
            if (update.Stage == GitHubCoordinationStage.Stale)
            {
                if (target.Value.Oid == current.State.TargetCommitOid)
                    return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
                effective = GitHubCoordinationStageUpdate.Stale(
                    current.State.TargetCommitOid, target.Value.Oid);
                intended = Apply(current.State, effective, current.HeadOid);
            }
            else if (checkTarget && target.Value.Oid != current.State.TargetCommitOid)
            {
                if (update.Stage is GitHubCoordinationStage.ContentCreated
                    or GitHubCoordinationStage.ProposalRefAdvanced)
                    effective = GitHubCoordinationStageUpdate.Stale(current.State.TargetCommitOid, target.Value.Oid);
            }
            var next = effective.Stage == GitHubCoordinationStage.Stale && update.Stage != GitHubCoordinationStage.Stale
                ? Apply(intended, effective, current.HeadOid)
                : intended;
            ValidateSuccessor(current.State, next);
            var prepared = Prepare(next);
            var orphan = await client.GetCommitAsync(prepared.CommitOid, cancellationToken).ConfigureAwait(false);
            if (orphan.Value is not null)
                return DomainFailure(GitHubCoordinationFailureKind.Unresolved);
            if (orphan.Failure?.Code != GitHubFailureCode.NotFound)
                return Failed(orphan);
            var objects = await CreateObjectsAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (objects is not null) return objects;

            var finalRef = await client.GetRefAsync(coordinationRef, cancellationToken).ConfigureAwait(false);
            if (finalRef.Value is null) return Failed(finalRef);
            if (finalRef.Value.Oid != current.HeadOid)
                return DomainFailure(GitHubCoordinationFailureKind.Conflict);
            var finalTarget = await client.GetRefAsync(authority.TargetRef, cancellationToken).ConfigureAwait(false);
            if (finalTarget.Value is null) return Failed(finalTarget);
            if ((effective.Stage is GitHubCoordinationStage.ContentCreated
                    or GitHubCoordinationStage.ProposalRefAdvanced)
                && finalTarget.Value.Oid != current.State.TargetCommitOid)
            {
                effective = GitHubCoordinationStageUpdate.Stale(current.State.TargetCommitOid, finalTarget.Value.Oid);
                next = Apply(intended, effective, current.HeadOid);
                ValidateSuccessor(current.State, next);
                prepared = Prepare(next);
                orphan = await client.GetCommitAsync(prepared.CommitOid, cancellationToken).ConfigureAwait(false);
                if (orphan.Value is not null)
                    return DomainFailure(GitHubCoordinationFailureKind.Unresolved);
                if (orphan.Failure?.Code != GitHubFailureCode.NotFound)
                    return Failed(orphan);
                objects = await CreateObjectsAsync(prepared, cancellationToken).ConfigureAwait(false);
                if (objects is not null) return objects;
            }
            var result = await UpdateAndReadAsync(repository.Value.Identity, finalTarget.Value,
                prepared, current.HeadOid, cancellationToken).ConfigureAwait(false);
            if (result.State is null) return result;
            return new(effective.Stage == GitHubCoordinationStage.Stale
                ? GitHubCoordinationOutcome.Stale : GitHubCoordinationOutcome.Advanced,
                state: result.State);
        }
        catch (GitHubCoordinationException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.InvalidInput);
        }
        catch (OperationCanceledException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.Cancelled));
        }
        catch
        {
            return DomainFailure(GitHubCoordinationFailureKind.Transport,
                new(GitHubFailureCode.HostFailure));
        }
    }

    private async ValueTask<GitHubCoordinationResult?> CreateObjectsAsync(
        GitHubPreparedCoordination prepared,
        CancellationToken cancellationToken)
    {
        var blob = await client.CreateBlobAsync(prepared.BlobOid,
            prepared.StateBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        var failure = await ReadBackObjectAsync(blob,
            token => client.GetBlobAsync(prepared.BlobOid, token),
            value => value.Oid == prepared.BlobOid
                && value.Bytes.AsSpan().SequenceEqual(prepared.StateBytes.AsSpan())).ConfigureAwait(false);
        if (failure is not null) return failure;

        var leaf = await client.CreateTreeAsync(prepared.LeafTreeOid,
            GitHubCoordinationObjects.LeafEntries(prepared), cancellationToken).ConfigureAwait(false);
        failure = await ReadBackObjectAsync(leaf,
            token => client.GetTreeAsync(prepared.LeafTreeOid, token),
            value => ExactLeaf(value, prepared)).ConfigureAwait(false);
        if (failure is not null) return failure;

        var root = await client.CreateTreeAsync(prepared.RootTreeOid,
            GitHubCoordinationObjects.RootEntries(prepared), cancellationToken).ConfigureAwait(false);
        failure = await ReadBackObjectAsync(root,
            token => client.GetTreeAsync(prepared.RootTreeOid, token),
            value => ExactRoot(value, prepared)).ConfigureAwait(false);
        if (failure is not null) return failure;

        var commit = await client.CreateCommitAsync(
            GitHubCoordinationObjects.CommitRequest(prepared), cancellationToken).ConfigureAwait(false);
        return await ReadBackObjectAsync(commit,
            token => client.GetCommitAsync(prepared.CommitOid, token),
            value => ExactCommit(value, prepared)).ConfigureAwait(false);
    }

    private async ValueTask<GitHubCoordinationResult?> ReadBackObjectAsync<TMutation, TRead>(
        GitHubApiResult<TMutation> mutation,
        Func<CancellationToken, ValueTask<GitHubApiResult<TRead>>> read,
        Func<TRead, bool> exact) where TMutation : class where TRead : class
    {
        if (mutation.Delivery == GitHubDelivery.NotDispatched)
            return Failed(mutation);
        if (mutation.Delivery is not (GitHubDelivery.NeedsReadback or GitHubDelivery.Ambiguous))
            return DomainFailure(GitHubCoordinationFailureKind.Unresolved,
                mutation.Failure, mutation.Delivery, mutation.Context, mutation.RequiredPermissions);
        using var recovery = new CancellationTokenSource(RecoveryTimeout);
        var observed = await read(recovery.Token).ConfigureAwait(false);
        var readbackFailure = RecoveryFailure(observed.Failure, recovery);
        if (observed.Value is not null)
            return exact(observed.Value) ? null
                : DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch,
                    mutation.Failure, mutation.Delivery, mutation.Context,
                    mutation.RequiredPermissions, readbackFailure);
        return DomainFailure(GitHubCoordinationFailureKind.Unresolved,
            mutation.Failure, mutation.Delivery, mutation.Context,
            mutation.RequiredPermissions, readbackFailure);
    }

    private async ValueTask<GitHubCoordinationResult> UpdateAndReadAsync(
        GitHubRepositoryIdentity repository,
        GitHubRef target,
        GitHubPreparedCoordination prepared,
        string beforeOid,
        CancellationToken cancellationToken)
    {
        var update = await client.UpdateRefAsync(new(coordinationRef, beforeOid,
            prepared.CommitOid, beforeOid == ZeroOid), cancellationToken).ConfigureAwait(false);
        if (update.Delivery == GitHubDelivery.NotDispatched) return Failed(update);
        if (update.Delivery is not (GitHubDelivery.NeedsReadback or GitHubDelivery.Ambiguous))
            return DomainFailure(GitHubCoordinationFailureKind.Unresolved,
                update.Failure, update.Delivery, update.Context, update.RequiredPermissions);
        using var recovery = new CancellationTokenSource(RecoveryTimeout);
        var read = await client.GetRefAsync(coordinationRef, recovery.Token).ConfigureAwait(false);
        var readbackFailure = RecoveryFailure(read.Failure, recovery);
        if (read.Value is null)
            return DomainFailure(GitHubCoordinationFailureKind.Unresolved,
                update.Failure, update.Delivery, update.Context,
                update.RequiredPermissions, readbackFailure);
        if (read.Value.Oid != prepared.CommitOid)
            return DomainFailure(GitHubCoordinationFailureKind.Conflict,
                update.Failure, update.Delivery, update.Context,
                update.RequiredPermissions, readbackFailure);
        var state = await ReadStateAsync(repository, target, read.Value.Oid,
            recovery.Token).ConfigureAwait(false);
        if (state.State is null)
            return DomainFailure(GitHubCoordinationFailureKind.Unresolved,
                update.Failure, update.Delivery, update.Context,
                update.RequiredPermissions,
                RecoveryFailure(state.Failure?.TransportFailure, recovery));
        var stateCapability = (StateCapability)state.State;
        if (stateCapability.HeadOid != prepared.CommitOid
            || !stateCapability.CanonicalBytes.AsSpan().SequenceEqual(prepared.StateBytes.AsSpan()))
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        return new(GitHubCoordinationOutcome.Advanced, state: stateCapability);
    }

    private async ValueTask<GitHubCoordinationResult> ReadStateAsync(
        GitHubRepositoryIdentity repository,
        GitHubRef target,
        string headOid,
        CancellationToken cancellationToken)
    {
        var current = await ReadStateObjectAsync(repository, target, headOid,
            cancellationToken).ConfigureAwait(false);
        if (current.State is null) return current;
        var currentState = (StateCapability)current.State;
        var operationId = currentState.State.OperationId;
        var cursor = currentState;
        var sameOperationTransitions = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal) { headOid };
        while (true)
        {
            var predecessorOid = cursor.State.CoordinationPredecessorOid;
            if (predecessorOid == ZeroOid)
            {
                if (cursor.Stage != GitHubCoordinationStage.Claimed
                    || cursor.State.Transition != "initial"
                    || operationId == authority.OperationId
                        && !ValidAuthorityRoot(cursor.State, null))
                    return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
                return current;
            }
            if (!visited.Add(predecessorOid))
                return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);

            var predecessor = await ReadStateObjectAsync(repository, target,
                predecessorOid, cancellationToken).ConfigureAwait(false);
            if (predecessor.State is null) return predecessor;
            var predecessorState = (StateCapability)predecessor.State;
            if (!ValidEdge(predecessorState.State, cursor.State))
                return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);

            if (predecessorState.State.OperationId != operationId)
            {
                if (cursor.Stage != GitHubCoordinationStage.Claimed
                    || operationId == authority.OperationId
                        && !ValidAuthorityRoot(cursor.State, predecessorState.State))
                    return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
                return current;
            }
            if (++sameOperationTransitions > MaximumSameOperationTransitions)
                return DomainFailure(GitHubCoordinationFailureKind.Bounds);
            cursor = predecessorState;
        }
    }

    private async ValueTask<GitHubCoordinationResult> ReadStateObjectAsync(
        GitHubRepositoryIdentity repository,
        GitHubRef target,
        string headOid,
        CancellationToken cancellationToken)
    {
        var commit = await client.GetCommitAsync(headOid, cancellationToken).ConfigureAwait(false);
        if (commit.Value is null) return Failed(commit);
        var root = await client.GetTreeAsync(commit.Value.TreeOid, cancellationToken).ConfigureAwait(false);
        if (root.Value is null) return Failed(root);
        if (root.Value.Entries.Length != 1) return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        var rootEntry = root.Value.Entries[0];
        if (rootEntry.Path != GitHubCoordinationObjects.RootPath
            || rootEntry.Mode != GitHubTreeMode.Directory || rootEntry.Size is not null)
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        var leaf = await client.GetTreeAsync(rootEntry.Oid, cancellationToken).ConfigureAwait(false);
        if (leaf.Value is null) return Failed(leaf);
        if (leaf.Value.Entries.Length != 1) return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        var leafEntry = leaf.Value.Entries[0];
        if (leafEntry.Path != GitHubCoordinationObjects.StatePath
            || leafEntry.Mode != GitHubTreeMode.File)
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        var blob = await client.GetBlobAsync(leafEntry.Oid, cancellationToken).ConfigureAwait(false);
        if (blob.Value is null) return Failed(blob);
        try
        {
            var state = GitHubCoordinationCodec.Decode(blob.Value.Bytes.AsMemory());
            var expected = GitHubCoordinationObjects.Prepare(state);
            GitHubCoordinationObjects.Authenticate(expected, commit.Value, root.Value, leaf.Value, blob.Value);
            if (!RepositoryMatches(state.RepositoryId, repository)
                || state.TargetRef != authority.TargetRef)
                return DomainFailure(GitHubCoordinationFailureKind.HumanChange);
            if (state.PullRequestCreationOperationCommitmentSha256 is not null)
                GitHubCoordinationCodec.ValidatePullRequestOwnership(state, ProposalRef(state));
            return new(GitHubCoordinationOutcome.Current,
                state: new StateCapability(this, repository, target, headOid,
                    state, expected.StateBytes));
        }
        catch (GitHubCoordinationException)
        {
            return DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch);
        }
    }

    private GitHubPreparedCoordination Prepare(GitHubCoordinationState state)
    {
        if (state.PullRequestCreationOperationCommitmentSha256 is not null)
            GitHubCoordinationCodec.ValidatePullRequestOwnership(state, ProposalRef(state));
        return GitHubCoordinationObjects.Prepare(state);
    }

    private static void ValidateSuccessor(
        GitHubCoordinationState current,
        GitHubCoordinationState next)
    {
        if (!ValidEdge(current, next))
            throw new GitHubCoordinationException();
    }

    private static bool Retains(string? current, string? next) =>
        current is null || current == next;

    private static bool ValidEdge(
        GitHubCoordinationState predecessor,
        GitHubCoordinationState current)
    {
        if (current.CoordinationPredecessorOid
            != GitHubCoordinationObjects.Prepare(predecessor).CommitOid)
            return false;
        if (predecessor.OperationId == current.OperationId)
            return SameOperation(predecessor, current)
                && AllowsStage(predecessor, current.Stage)
                && RetainsEdge(predecessor, current)
                && ValidStageEdge(predecessor, current);
        if (current.Stage != GitHubCoordinationStage.Claimed)
            return false;
        return (predecessor.Stage, current.Transition) switch
        {
            (GitHubCoordinationStage.Published, "same-snapshot-append") =>
                current.PrecedingOperationId == predecessor.OperationId
                && current.PrecedingAuthorityCommitmentSha256 == predecessor.AuthorityCommitmentSha256
                && current.PrecedingCandidateCommitmentSha256 == predecessor.CurrentCandidateCommitmentSha256
                && current.GenerationId == predecessor.GenerationId
                && current.SnapshotCommitmentSha256 == predecessor.SnapshotCommitmentSha256
                && current.PolicyCommitmentSha256 == predecessor.PolicyCommitmentSha256
                && current.TargetCommitOid == predecessor.TargetCommitOid
                && current.RepositoryId.Equals(predecessor.RepositoryId,
                    StringComparison.OrdinalIgnoreCase)
                && current.TargetRef == predecessor.TargetRef,
            (GitHubCoordinationStage.Merged, "successor-after-merge") =>
                SameRepositoryAndRef(predecessor, current),
            (GitHubCoordinationStage.ClosedUnmerged, "successor-after-closed-unmerged") =>
                SameRepositoryAndRef(predecessor, current),
            (GitHubCoordinationStage.Stale, "initial") =>
                current.GenerationId != predecessor.GenerationId
                && SameRepositoryAndRef(predecessor, current),
            _ => false,
        };
    }

    private bool ValidAuthorityRoot(
        GitHubCoordinationState root,
        GitHubCoordinationState? predecessor)
    {
        if (!MatchesAuthority(root)
            || predecessor is null && root.CoordinationPredecessorOid != ZeroOid
            || predecessor is not null && !ValidEdge(predecessor, root))
            return false;
        return (authority.Transition, predecessor?.Stage) switch
        {
            (GitHubPublicationTransitionKind.Initial, null) => true,
            (GitHubPublicationTransitionKind.Initial, GitHubCoordinationStage.Stale) => true,
            (GitHubPublicationTransitionKind.SameSnapshotAppend, GitHubCoordinationStage.Published) =>
                MatchesAppendPredecessor(predecessor),
            (GitHubPublicationTransitionKind.SuccessorAfterMerge, GitHubCoordinationStage.Merged) =>
                MatchesTerminalPredecessor(predecessor),
            (GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
                GitHubCoordinationStage.ClosedUnmerged) => MatchesTerminalPredecessor(predecessor),
            _ => false,
        };
    }

    private static bool SameRepositoryAndRef(
        GitHubCoordinationState predecessor,
        GitHubCoordinationState current) =>
        current.RepositoryId.Equals(predecessor.RepositoryId, StringComparison.OrdinalIgnoreCase)
        && current.TargetRef == predecessor.TargetRef;

    private static bool SameOperation(
        GitHubCoordinationState first,
        GitHubCoordinationState second) =>
        first.RepositoryId == second.RepositoryId
        && first.TargetRef == second.TargetRef
        && first.TargetCommitOid == second.TargetCommitOid
        && first.SnapshotCommitmentSha256 == second.SnapshotCommitmentSha256
        && first.AuthorityCommitmentSha256 == second.AuthorityCommitmentSha256
        && first.PolicyCommitmentSha256 == second.PolicyCommitmentSha256
        && first.OperationCommitmentSha256 == second.OperationCommitmentSha256
        && first.CurrentCandidateCommitmentSha256 == second.CurrentCandidateCommitmentSha256
        && first.PrecedingOperationId == second.PrecedingOperationId
        && first.PrecedingAuthorityCommitmentSha256 == second.PrecedingAuthorityCommitmentSha256
        && first.PrecedingCandidateCommitmentSha256 == second.PrecedingCandidateCommitmentSha256
        && first.GenerationId == second.GenerationId
        && first.Transition == second.Transition
        && first.CumulativeDocumentationBlocks == second.CumulativeDocumentationBlocks
        && first.CumulativePatchBytes == second.CumulativePatchBytes
        && first.CumulativeChangedFiles.SequenceEqual(second.CumulativeChangedFiles);

    private static bool RetainsEdge(
        GitHubCoordinationState predecessor,
        GitHubCoordinationState current) =>
        Retains(predecessor.ContentCommitOid, current.ContentCommitOid)
        && Retains(predecessor.ProposalRefOid, current.ProposalRefOid)
        && Retains(predecessor.ProposalCommitOid, current.ProposalCommitOid)
        && Retains(predecessor.ProposalTreeOid, current.ProposalTreeOid)
        && Retains(predecessor.PullRequestCreationOperationCommitmentSha256,
            current.PullRequestCreationOperationCommitmentSha256)
        && (predecessor.PullRequestNumber is null
            || predecessor.PullRequestNumber == current.PullRequestNumber)
        && Retains(predecessor.ExpectedBaseOid, current.ExpectedBaseOid)
        && Retains(predecessor.ObservedBaseOid, current.ObservedBaseOid)
        && Retains(predecessor.OwnershipMarkerSha256, current.OwnershipMarkerSha256);

    private static bool ValidStageEdge(
        GitHubCoordinationState predecessor,
        GitHubCoordinationState current)
    {
        if (current.ExpectedBaseOid is not null
            && current.ExpectedBaseOid != current.TargetCommitOid)
            return false;
        if (current.Stage != GitHubCoordinationStage.Stale) return true;
        return predecessor.Stage switch
        {
            GitHubCoordinationStage.Claimed => current.ProposalRefOid is null
                && current.ProposalCommitOid is null && current.ProposalTreeOid is null,
            GitHubCoordinationStage.ContentCreated =>
                current.ContentCommitOid == predecessor.ContentCommitOid
                && (current.ProposalRefOid is null && current.ProposalCommitOid is null
                    && current.ProposalTreeOid is null
                    || current.ProposalRefOid == current.ContentCommitOid
                    && current.ProposalCommitOid == current.ContentCommitOid
                    && current.ProposalTreeOid is not null),
            GitHubCoordinationStage.ProposalRefAdvanced =>
                current.ContentCommitOid == predecessor.ContentCommitOid
                && current.ProposalRefOid == predecessor.ProposalRefOid
                && current.ProposalCommitOid == predecessor.ProposalCommitOid
                && current.ProposalTreeOid == predecessor.ProposalTreeOid,
            _ => false,
        };
    }

    private GitHubCoordinationState Apply(
        GitHubCoordinationState current,
        GitHubCoordinationStageUpdate update,
        string predecessor)
    {
        return update.Stage switch
        {
            GitHubCoordinationStage.ContentCreated => GitHubCoordinationCodec.WithStage(
                current, update.Stage, predecessor, update.ContentCommitOid),
            GitHubCoordinationStage.ProposalRefAdvanced => GitHubCoordinationCodec.WithStage(
                current, update.Stage, predecessor, update.ContentCommitOid,
                update.ProposalRefOid, update.ProposalCommitOid, update.ProposalTreeOid),
            GitHubCoordinationStage.PullRequestCreated or GitHubCoordinationStage.Published
                or GitHubCoordinationStage.StaleDraft => GitHubCoordinationCodec.WithStage(
                    current, update.Stage, predecessor, update.ContentCommitOid,
                    update.ProposalRefOid, update.ProposalCommitOid, update.ProposalTreeOid,
                    update.PullRequestCreationOperationCommitmentSha256, update.PullRequestNumber,
                    update.ExpectedBaseOid, update.ObservedBaseOid, update.OwnershipMarkerSha256),
            GitHubCoordinationStage.Stale => GitHubCoordinationCodec.WithStage(
                current, update.Stage, predecessor, current.ContentCommitOid,
                current.ProposalRefOid, current.ProposalCommitOid, current.ProposalTreeOid,
                expectedBaseOid: update.ExpectedBaseOid, observedBaseOid: update.ObservedBaseOid),
            GitHubCoordinationStage.AwaitingReview or GitHubCoordinationStage.Merged
                or GitHubCoordinationStage.ClosedUnmerged => GitHubCoordinationCodec.WithStage(
                    current, update.Stage, predecessor, current.ContentCommitOid,
                    current.ProposalRefOid, current.ProposalCommitOid, current.ProposalTreeOid,
                    current.PullRequestCreationOperationCommitmentSha256, current.PullRequestNumber,
                    current.ExpectedBaseOid, current.ObservedBaseOid, current.OwnershipMarkerSha256),
            _ => throw new GitHubCoordinationException(),
        };
    }

    private bool MatchesAppendPredecessor(GitHubCoordinationState state) =>
        authority.PrecedingOperationId == state.OperationId
        && authority.PrecedingAuthorityCommitmentSha256 == state.AuthorityCommitmentSha256
        && authority.PrecedingCandidateCommitmentSha256 == state.CurrentCandidateCommitmentSha256
        && authority.PrecedingGenerationId == state.GenerationId
        && authority.PrecedingSnapshotCommitmentSha256 == state.SnapshotCommitmentSha256
        && authority.PrecedingPolicyCommitmentSha256 == state.PolicyCommitmentSha256
        && authority.ExpectedBaseCommitOid == state.TargetCommitOid
        && authority.PrecedingChangedFiles.Length == state.CumulativeChangedFiles.Length
        && authority.PrecedingChangedFiles.Zip(state.CumulativeChangedFiles)
            .All(pair => pair.First.Path == pair.Second.Path
                && pair.First.CandidateFileSha256 == pair.Second.CandidateSha256);

    private bool MatchesTerminalPredecessor(GitHubCoordinationState state)
    {
        var predecessor = authority.TerminalPredecessor;
        return predecessor is not null && predecessor.PullRequestNumber == state.PullRequestNumber
            && predecessor.GenerationId == state.GenerationId
            && predecessor.HeadOid == state.ProposalCommitOid
            && predecessor.Disposition == (state.Stage == GitHubCoordinationStage.Merged
                ? GitHubPublicationPredecessorDisposition.Merged
                : GitHubPublicationPredecessorDisposition.ClosedUnmerged);
    }

    private bool MatchesAuthority(GitHubCoordinationState state) =>
        state.RepositoryId == authority.RepositoryOwner + "/" + authority.RepositoryName
        && state.TargetRef == authority.TargetRef
        && state.TargetCommitOid == authority.ExpectedBaseCommitOid
        && state.SnapshotCommitmentSha256 == authority.SnapshotCommitmentSha256
        && state.AuthorityCommitmentSha256 == authority.AuthorityCommitmentSha256
        && state.PolicyCommitmentSha256 == authority.PolicyCommitmentSha256
        && state.OperationId == authority.OperationId
        && state.OperationCommitmentSha256 == authority.OperationCommitmentSha256
        && state.CurrentCandidateCommitmentSha256 == authority.CandidateCommitmentSha256
        && state.PrecedingOperationId == authority.PrecedingOperationId
        && state.PrecedingAuthorityCommitmentSha256 == authority.PrecedingAuthorityCommitmentSha256
        && state.PrecedingCandidateCommitmentSha256 == authority.PrecedingCandidateCommitmentSha256
        && state.GenerationId == authority.GenerationId
        && state.Transition == Transition(authority.Transition)
        && state.CumulativeDocumentationBlocks == authority.CumulativeDocumentationBlocks
        && state.CumulativePatchBytes == authority.CumulativePatchBytes
        && authority.ChangedFiles.Length == state.CumulativeChangedFiles.Length
        && authority.ChangedFiles.Zip(state.CumulativeChangedFiles)
            .All(pair => pair.First.Path == pair.Second.Path
                && pair.First.CandidateFileSha256 == pair.Second.CandidateSha256);

    private static bool AllowsStage(GitHubCoordinationState current, GitHubCoordinationStage next) =>
        (current.Stage, next) switch
        {
            (GitHubCoordinationStage.Claimed, GitHubCoordinationStage.ContentCreated or GitHubCoordinationStage.Stale) => true,
            (GitHubCoordinationStage.ContentCreated, GitHubCoordinationStage.ProposalRefAdvanced or GitHubCoordinationStage.Stale) => true,
            (GitHubCoordinationStage.ProposalRefAdvanced, GitHubCoordinationStage.PullRequestCreated
                or GitHubCoordinationStage.Published or GitHubCoordinationStage.StaleDraft
                or GitHubCoordinationStage.Stale) => true,
            (GitHubCoordinationStage.PullRequestCreated, GitHubCoordinationStage.Published) => true,
            (GitHubCoordinationStage.Published, GitHubCoordinationStage.AwaitingReview
                or GitHubCoordinationStage.Merged or GitHubCoordinationStage.ClosedUnmerged) => true,
            (GitHubCoordinationStage.AwaitingReview, GitHubCoordinationStage.Merged
                or GitHubCoordinationStage.ClosedUnmerged) => true,
            (GitHubCoordinationStage.StaleDraft, GitHubCoordinationStage.Merged
                or GitHubCoordinationStage.ClosedUnmerged) => true,
            _ => false,
        };

    private static bool CanBecomeStale(GitHubCoordinationStage stage) => stage is
        GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated
        or GitHubCoordinationStage.ProposalRefAdvanced;

    private static bool PermitsCreate(GitHubCoordinationStage stage) => stage is
        GitHubCoordinationStage.Claimed or GitHubCoordinationStage.ContentCreated
        or GitHubCoordinationStage.ProposalRefAdvanced;

    private static bool ExactLeaf(GitHubTree tree, GitHubPreparedCoordination prepared) =>
        tree.Oid == prepared.LeafTreeOid && tree.Entries.Length == 1
        && tree.Entries[0].Path == GitHubCoordinationObjects.StatePath
        && tree.Entries[0].Mode == GitHubTreeMode.File
        && tree.Entries[0].Oid == prepared.BlobOid
        && (tree.Entries[0].Size is null || tree.Entries[0].Size == prepared.StateBytes.Length);

    private static bool ExactRoot(GitHubTree tree, GitHubPreparedCoordination prepared) =>
        tree.Oid == prepared.RootTreeOid && tree.Entries.Length == 1
        && tree.Entries[0].Path == GitHubCoordinationObjects.RootPath
        && tree.Entries[0].Mode == GitHubTreeMode.Directory
        && tree.Entries[0].Oid == prepared.LeafTreeOid && tree.Entries[0].Size is null;

    private static bool ExactCommit(GitHubCommit commit, GitHubPreparedCoordination prepared) =>
        commit.Oid == prepared.CommitOid && commit.TreeOid == prepared.RootTreeOid
        && commit.Parents.Length == 1 && commit.Parents[0] == prepared.ParentOid
        && commit.Message == prepared.Message
        && ExactActor(commit.Author) && ExactActor(commit.Committer);

    private static bool ExactActor(GitHubCommitActor actor) =>
        actor.Name == GitHubCoordinationObjects.ActorName
        && actor.Email == GitHubCoordinationObjects.ActorEmail
        && actor.Date.ToUnixTimeSeconds() == GitHubCoordinationObjects.ActorUnixSeconds
        && actor.Date.Offset == TimeSpan.Zero;

    private string ProposalRef(GitHubCoordinationState state)
    {
        var parts = state.RepositoryId.Split('/');
        var campaign = IdentityKey("proposal-campaign", parts[0].ToLowerInvariant(),
            parts[1].ToLowerInvariant(), state.TargetRef, authority.CampaignLineage);
        var generation = IdentityKey("proposal-generation", authority.CampaignLineage,
            state.GenerationId, state.SnapshotCommitmentSha256, state.PolicyCommitmentSha256);
        return "refs/heads/contract-scribe/proposals/" + campaign + "/" + generation;
    }

    private static string IdentityKey(string domain, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes("domain"));
        Append(hash, Encoding.UTF8.GetBytes("contract-scribe/github-" + domain + "/v1"));
        foreach (var value in values)
        {
            Append(hash, Encoding.UTF8.GetBytes("value"));
            Append(hash, Encoding.UTF8.GetBytes(value));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static bool RepositoryMatches(string repositoryId, GitHubRepositoryIdentity repository) =>
        repositoryId.Equals(repository.Owner + "/" + repository.Name, StringComparison.OrdinalIgnoreCase);

    private static bool SameState(StateCapability expected, StateCapability actual) =>
        expected.Repository == actual.Repository && expected.HeadOid == actual.HeadOid
        && expected.CanonicalBytes.AsSpan().SequenceEqual(actual.CanonicalBytes.AsSpan());

    private static string Transition(GitHubPublicationTransitionKind transition) => transition switch
    {
        GitHubPublicationTransitionKind.Initial => "initial",
        GitHubPublicationTransitionKind.SameSnapshotAppend => "same-snapshot-append",
        GitHubPublicationTransitionKind.SuccessorAfterMerge => "successor-after-merge",
        GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged => "successor-after-closed-unmerged",
        _ => throw new GitHubCoordinationException(),
    };

    private static GitHubCoordinationResult Failed<T>(
        GitHubApiResult<T> result,
        GitHubCoordinationFailureKind kind = GitHubCoordinationFailureKind.Transport) where T : class =>
        DomainFailure(kind, result.Failure, result.Delivery, result.Context, result.RequiredPermissions);

    private static GitHubFailure? RecoveryFailure(
        GitHubFailure? failure,
        CancellationTokenSource recovery) =>
        recovery.IsCancellationRequested && failure?.Code == GitHubFailureCode.Cancelled
            ? failure with { Code = GitHubFailureCode.Timeout }
            : failure;

    private static GitHubCoordinationResult DomainFailure(
        GitHubCoordinationFailureKind kind,
        GitHubFailure? failure = null,
        GitHubDelivery delivery = GitHubDelivery.NotDispatched,
        GitHubMutationContext? context = null,
        GitHubPermissionAlternatives? permissions = null,
        GitHubFailure? readbackFailure = null) => new(
            kind is GitHubCoordinationFailureKind.DifferentOperation
                or GitHubCoordinationFailureKind.Conflict
                or GitHubCoordinationFailureKind.StageConflict
                or GitHubCoordinationFailureKind.HumanChange
                ? GitHubCoordinationOutcome.Conflict : GitHubCoordinationOutcome.Failed,
            failure: new(kind, failure, delivery, context, permissions, readbackFailure));

    private sealed class ReadCapability : IGitHubCoordinationReadCapability
    {
        private readonly GitHubCoordinationStore owner;
        private readonly GitHubRepositoryIdentity repository;
        private readonly GitHubRef target;
        private readonly StateCapability? current;
        internal ReadCapability(GitHubCoordinationStore owner, GitHubRepositoryIdentity repository,
            GitHubRef target, StateCapability? current)
        { this.owner = owner; this.repository = repository; this.target = target; this.current = current; }
        internal GitHubCoordinationStore Owner => owner;
        internal GitHubRepositoryIdentity Repository => repository;
        internal GitHubRef Target => target;
        internal StateCapability? Current => current;
        public override string ToString() => nameof(ReadCapability);
    }

    private sealed class StateCapability : IGitHubCoordinationStateCapability
    {
        private readonly GitHubCoordinationStore owner;
        private readonly GitHubRepositoryIdentity repository;
        private readonly GitHubRef target;
        private readonly string headOid;
        private readonly GitHubCoordinationState state;
        private readonly ImmutableArray<byte> canonicalBytes;
        internal StateCapability(GitHubCoordinationStore owner, GitHubRepositoryIdentity repository,
            GitHubRef target, string headOid, GitHubCoordinationState state,
            ImmutableArray<byte> canonicalBytes)
        { this.owner = owner; this.repository = repository; this.target = target; this.headOid = headOid; this.state = state; this.canonicalBytes = canonicalBytes; }
        internal GitHubCoordinationStore Owner => owner;
        public GitHubRepositoryIdentity Repository => repository;
        internal GitHubRef Target => target;
        public string HeadOid => headOid;
        public GitHubCoordinationStage Stage => state.Stage;
        public string TargetRef => state.TargetRef;
        public string TargetCommitOid => state.TargetCommitOid;
        public string SnapshotCommitmentSha256 => state.SnapshotCommitmentSha256;
        public string AuthorityCommitmentSha256 => state.AuthorityCommitmentSha256;
        public string PolicyCommitmentSha256 => state.PolicyCommitmentSha256;
        public string OperationId => state.OperationId;
        public string OperationCommitmentSha256 => state.OperationCommitmentSha256;
        public string CurrentCandidateCommitmentSha256 => state.CurrentCandidateCommitmentSha256;
        public string? PrecedingOperationId => state.PrecedingOperationId;
        public string? PrecedingAuthorityCommitmentSha256 => state.PrecedingAuthorityCommitmentSha256;
        public string? PrecedingCandidateCommitmentSha256 => state.PrecedingCandidateCommitmentSha256;
        public string GenerationId => state.GenerationId;
        public string Transition => state.Transition;
        public string CoordinationPredecessorOid => state.CoordinationPredecessorOid;
        public string? ContentCommitOid => state.ContentCommitOid;
        public string? ProposalRefOid => state.ProposalRefOid;
        public string? ProposalCommitOid => state.ProposalCommitOid;
        public string? ProposalTreeOid => state.ProposalTreeOid;
        public string? PullRequestCreationOperationCommitmentSha256 =>
            state.PullRequestCreationOperationCommitmentSha256;
        public int? PullRequestNumber => state.PullRequestNumber;
        public string? ExpectedBaseOid => state.ExpectedBaseOid;
        public string? ObservedBaseOid => state.ObservedBaseOid;
        public string? OwnershipMarkerSha256 => state.OwnershipMarkerSha256;
        public int CumulativeDocumentationBlocks => state.CumulativeDocumentationBlocks;
        public long CumulativePatchBytes => state.CumulativePatchBytes;
        public ImmutableArray<GitHubCoordinationChangedFile> CumulativeChangedFiles =>
            state.CumulativeChangedFiles;
        internal GitHubCoordinationState State => state;
        internal ImmutableArray<byte> CanonicalBytes => canonicalBytes;
        public override string ToString() => nameof(StateCapability);
    }

    private sealed class GuardCapability : IGitHubCoordinationGuardCapability
    {
        private readonly GitHubCoordinationStore owner;
        private readonly StateCapability state;
        internal GuardCapability(GitHubCoordinationStore owner, StateCapability state)
        { this.owner = owner; this.state = state; }
        internal GitHubCoordinationStore Owner => owner;
        public IGitHubCoordinationStateCapability State => state;
        public override string ToString() => nameof(GuardCapability);
    }
}
