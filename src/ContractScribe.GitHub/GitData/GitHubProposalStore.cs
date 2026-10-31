using System.Collections.Immutable;
using Graph = ContractScribe.GitHub.GitData.GitHubProposalObjectGraph;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using static ContractScribe.GitHub.GitData.GitHubProposalObjects;

namespace ContractScribe.GitHub.GitData;

internal interface IGitHubPreparedProposal;
internal interface IGitHubInspectedProposal
{
    string Ref { get; }
    string CommitOid { get; }
    string TreeOid { get; }
    string BeforeOid { get; }
    string ObservedRefOid { get; }
    bool ContentExists { get; }
}
internal interface IGitHubProposalContent
{
    string CommitOid { get; }
    string TreeOid { get; }
    string Ref { get; }
}

internal enum GitHubProposalOutcome { Prepared, ContentVerified, RefVerified, RecordedVerified, Failed }
internal enum GitHubProposalFailureKind { InvalidInput, Integrity, Bounds, Conflict, Unresolved, Transport }
internal sealed record GitHubProposalFailure(GitHubProposalFailureKind Kind,
    GitHubFailure? Transport = null, GitHubDelivery Delivery = GitHubDelivery.NotDispatched,
    GitHubMutationContext? Context = null, GitHubPermissionAlternatives? Permissions = null,
    GitHubFailure? Readback = null, GitHubCoordinationFailureKind? CoordinationCause = null) : GitHubValue;
internal sealed record GitHubProposalObservation(GitHubRepositoryIdentity Repository, string OperationCommitment,
    string ExpectedBaseOid, string ObservedBaseOid, string CommitOid, string TreeOid,
    string Ref, string ObservedRefOid) : GitHubValue;
internal sealed record GitHubProposalResult(GitHubProposalOutcome Outcome,
    IGitHubPreparedProposal? Prepared = null, IGitHubProposalContent? Content = null,
    GitHubProposalFailure? Failure = null, GitHubProposalObservation? Observation = null,
    IGitHubInspectedProposal? Inspection = null, GitHubCoordinationResult? Coordination = null) : GitHubValue;

internal sealed class GitHubProposalStore
{
    private readonly GitHubApiClient client;
    private readonly GitHubCoordinationStore coordination;
    private readonly ValidatedGitHubPublicationAuthority authority;
    private readonly string proposalRef;
    private readonly string generationKey;
    private readonly Func<IGitHubCoordinationGuardCapability, CancellationToken, ValueTask<GitHubProposalFailure?>>? mutationPreflight;
    private const string Zero = GitHubPublicationContract.MissingGitObjectId;

    private GitHubProposalStore(GitHubApiClient client, GitHubCoordinationStore coordination,
        Func<IGitHubCoordinationGuardCapability, CancellationToken, ValueTask<GitHubProposalFailure?>>? mutationPreflight)
    {
        this.client = client;
        this.coordination = coordination;
        this.mutationPreflight = mutationPreflight;
        authority = client.Authority;
        proposalRef = GitHubPublicationFactory.CreateProposalRef(authority);
        generationKey = proposalRef[(proposalRef.LastIndexOf('/') + 1)..];
    }

    internal static GitHubProposalStore Create(GitHubApiClient client, GitHubCoordinationStore coordination,
        Func<IGitHubCoordinationGuardCapability, CancellationToken, ValueTask<GitHubProposalFailure?>>? mutationPreflight = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(coordination);
        if (!coordination.UsesClient(client)) throw new ArgumentException("Invalid publication store binding.");
        return new(client, coordination, mutationPreflight);
    }

    internal static bool Authenticates(IGitHubInspectedProposal proof, GitHubCoordinationStore coordination,
        IGitHubCoordinationStateCapability state) => proof is Inspection owned
        && ReferenceEquals(owned.Owner.coordination, coordination) && coordination.Owns(state)
        && owned.ContextHead is { } head && coordination.ResolvePublicationHead(head) == state.HeadOid;

    internal ValueTask<GitHubProposalResult> InspectAsync(IGitHubCoordinationReadCapability read,
        ValidatedGitHubChangedFilePayload payload, CancellationToken token = default) => Run(async () =>
    {
        var context = coordination.Inspect(read);
        Require(context is not null);
        var fresh = await coordination.RecheckAsync(read, token);
        if (fresh.Read is null) CoordinationFailure(fresh);
        var state = fresh.State;
        if (state is not null && state.OperationCommitmentSha256 == authority.OperationCommitmentSha256)
        {
            var prepared = await PrepareCore(state, payload, readOnly: true, token);
            var currentResult = await InspectPlan((PreparedCapability)prepared.Prepared!, state.HeadOid, state.ContentCommitOid is not null, token);
            Require(state.ProposalCommitOid is null || currentResult.Inspection!.ObservedRefOid == state.ProposalCommitOid);
            fresh = await coordination.RecheckAsync(read, token);
            if (fresh.Read is null) CoordinationFailure(fresh);
            return currentResult;
        }
        // Prospective local candidate validation precedes admission, but grants no write capability.
        var bytes = PayloadBytes(payload);
        var baseTree = Value(await coordination.ReadBaseTreeAsync(token));
        var basis = await ReadBasis(baseTree.Oid, token);
        var before = Zero;
        Require(before is not null);
        var oids = bytes.ToDictionary(p => p.Key, p => ObjectOid("blob", p.Value.AsSpan()), StringComparer.Ordinal);
        var graph = Overlay(basis, oids);
        var commit = Commit(graph.RootOid, before == Zero ? authority.ExpectedBaseCommitOid : before!,
            authority.OperationCommitmentSha256, generationKey);
        var plan = new PreparedCapability(this, context!.Repository, basis.RootOid, before!, commit, graph, bytes,
            oids, basis.Trees.Values.Select(TreeOid).ToHashSet(StringComparer.Ordinal), true);
        var result = await InspectPlan(plan, state?.HeadOid, recorded: false, token);
        fresh = await coordination.RecheckAsync(read, token);
        if (fresh.Read is null) CoordinationFailure(fresh);
        return result;
    });

    internal ValueTask<GitHubProposalResult> InspectPredecessorAsync(IGitHubCoordinationStateCapability current,
        CancellationToken token = default) => Run(async () =>
    {
        var fresh = await coordination.InspectStateAsync(current, token);
        if (fresh.State is null) CoordinationFailure(fresh);
        current = fresh.State!;
        var source = current;
        Require(source is not null && coordination.Owns(source));
        var reference = coordination.ProposalRefFor(source!)!;
        var head = await coordination.ReadDataRefAsync(reference, token);
        var observed = head.Value?.Oid;
        if (observed is null && head.Failure?.Code != GitHubFailureCode.NotFound) Throw(head);
        observed ??= Zero;
        var oid = source!.ProposalCommitOid ?? source.ContentCommitOid;
        if (oid is null && observed == Zero)
        {
            Require(source.Stage is GitHubCoordinationStage.Stale or GitHubCoordinationStage.Claimed);
            return new(GitHubProposalOutcome.RecordedVerified,
                Inspection: new Inspection(this, current.HeadOid, reference, Zero, Zero, Zero, observed, false));
        }
        oid ??= observed;
        var commit = Value(await client.GetCommitAsync(oid, token));
        var basisCommit = Value(await client.GetCommitAsync(source.TargetCommitOid, token));
        var basis = await ReadGraph(basisCommit.TreeOid, token);
        var graph = await ReadGraph(commit.TreeOid, token);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in source.CumulativeChangedFiles)
        {
            Require(basis.Files.TryGetValue(file.Path, out var original) && Regular(original.Mode)
                && graph.Files.TryGetValue(file.Path, out var published) && published.Mode == original.Mode);
            var entry = graph.Files[file.Path];
            await CheckBlob(entry, file.CandidateSha256, token);
            replacements.Add(file.Path, entry.Oid);
        }
        Require(Overlay(basis, replacements).RootOid == graph.RootOid);
        var parent = source.TargetCommitOid;
        Require(parent is not null && ExactCommit(commit, Commit(graph.RootOid, parent!,
            source.OperationCommitmentSha256, reference[(reference.LastIndexOf('/') + 1)..])));
        Require(source.ProposalTreeOid is null || source.ProposalTreeOid == graph.RootOid);
        Require(source.ProposalCommitOid is null ? observed == Zero || observed == oid : observed == oid);
        fresh = await coordination.InspectStateAsync(current, token);
        if (fresh.State is null) CoordinationFailure(fresh);
        return new(GitHubProposalOutcome.RecordedVerified,
            Inspection: new Inspection(this, current.HeadOid, reference, oid, graph.RootOid, parent!, observed, true));
    });

    private Dictionary<string, ImmutableArray<byte>> PayloadBytes(ValidatedGitHubChangedFilePayload payload)
    {
        Require(payload is not null && payload.AuthorityCommitmentSha256 == authority.AuthorityCommitmentSha256
            && payload.Files.Length == authority.ChangedFiles.Length);
        var result = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in payload!.Files)
        {
            Require(!file.CandidateBytes.IsDefault && file.CandidateBytes.Length <= GitHubPublicationContract.MaximumPayloadBytesPerFile);
            total = checked(total + file.CandidateBytes.Length);
            Require(total <= GitHubPublicationContract.MaximumAggregatePayloadBytes && result.TryAdd(file.Path, file.CandidateBytes));
        }
        foreach (var file in authority.ChangedFiles)
            Require(result.TryGetValue(file.Path, out var bytes) && Sha256(bytes.AsSpan()) == file.CandidateFileSha256);
        return result;
    }

    private async ValueTask<GitHubProposalResult> InspectPlan(PreparedCapability plan, string? contextHead,
        bool recorded, CancellationToken token)
    {
        var commit = await coordination.ReadVerifiedProposalCommitAsync(plan.Commit, token);
        if (commit.Value is not null) await VerifyContent(plan, token, commit.Value);
        else if (recorded || commit.Failure?.Code != GitHubFailureCode.NotFound) Throw(commit);
        var reference = await ReadRef(token);
        Require(reference == plan.BeforeOid || reference == plan.Commit.ExpectedOid);
        Require(reference != plan.Commit.ExpectedOid || commit.Value is not null);
        return new(GitHubProposalOutcome.RecordedVerified, Inspection: new Inspection(this, contextHead, proposalRef,
            plan.Commit.ExpectedOid, plan.Commit.TreeOid, plan.BeforeOid, reference, commit.Value is not null));
    }

    private static void CoordinationFailure(GitHubCoordinationResult result) =>
        throw new FailureException(new(GitHubProposalFailureKind.Conflict, result.Failure?.TransportFailure,
            result.Failure?.Delivery ?? GitHubDelivery.NotDispatched, result.Failure?.Context,
            result.Failure?.Permissions, result.Failure?.ReadbackFailure, result.Failure?.Kind));

    internal ValueTask<GitHubProposalResult> PrepareAsync(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default) =>
        Run(() => PrepareCore(state, payload, readOnly: false, cancellationToken));

    private async ValueTask<GitHubProposalResult> PrepareCore(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, bool readOnly, CancellationToken cancellationToken)
    {
        var bytes = PayloadBytes(payload);

        var guarded = (await Guard(state, cancellationToken, readOnly)).State!;
        var baseTree = Value(await coordination.ReadBaseTreeAsync(cancellationToken));
        var basis = await ReadBasis(baseTree.Oid, cancellationToken);

        var before = Zero;
        var blobOids = bytes.ToDictionary(pair => pair.Key, pair => ObjectOid("blob", pair.Value.AsSpan()), StringComparer.Ordinal);
        var overlay = Overlay(basis, blobOids);
        var commit = Commit(overlay.RootOid, before == Zero ? authority.ExpectedBaseCommitOid : before,
            authority.OperationCommitmentSha256, generationKey);
        Require(state.ContentCommitOid is null || state.ContentCommitOid == commit.ExpectedOid);
        var prepared = new PreparedCapability(this, guarded.Repository, basis.RootOid, before, commit, overlay, bytes, blobOids,
            basis.Trees.Values.Select(TreeOid).ToHashSet(StringComparer.Ordinal), readOnly);
        await CheckGate(prepared, state, allowSuccessor: true, cancellationToken);
        return new(GitHubProposalOutcome.Prepared, Prepared: prepared);
    }

    internal ValueTask<GitHubProposalResult> VerifyRecordedAsync(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default) => Run(async () =>
    {
        Require(state is not null && state.ContentCommitOid is not null);
        var prepared = await PrepareCore(state!, payload, readOnly: true, cancellationToken);
        var plan = (PreparedCapability)prepared.Prepared!;
        await VerifyContent(plan, cancellationToken);
        var current = (await Guard(state!, cancellationToken, readOnly: true)).State!;
        var observedRef = await ReadRef(cancellationToken);
        return new(GitHubProposalOutcome.RecordedVerified, Observation: new(plan.Repository,
            authority.OperationCommitmentSha256, authority.ExpectedBaseCommitOid, coordination.ObservedTargetOid(current)!,
            plan.Commit.ExpectedOid, plan.Commit.TreeOid, proposalRef, observedRef));
    });

    internal ValueTask<GitHubProposalResult> CreateContentAsync(IGitHubPreparedProposal prepared,
        IGitHubCoordinationStateCapability state, CancellationToken cancellationToken = default) => Run(async () =>
    {
        Require(prepared is PreparedCapability owned && ReferenceEquals(owned.Owner, this) && !owned.ReadOnly);
        var plan = (PreparedCapability)prepared;
        await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
        var existing = await coordination.ReadVerifiedProposalCommitAsync(plan.Commit, cancellationToken);
        if (existing.Value is not null)
        {
            await VerifyContent(plan, cancellationToken, existing.Value);
            await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
            return new(GitHubProposalOutcome.ContentVerified, Content: new ContentCapability(plan));
        }
        if (existing.Failure?.Code != GitHubFailureCode.NotFound) Throw(existing);
        Require(state.Stage == GitHubCoordinationStage.Claimed && state.ContentCommitOid is null);
        // Only actual remote object observations from this lexical upload stage.
        // Prepared bytes cannot satisfy the subsequent complete graph proof.
        var observedObjects = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        // This lexical stage owns only content for an authenticated prepared
        // plan whose commit was just physically absent. It grants no ref/PR
        // right. Initial and final full visibility gates remain above/below.
        async ValueTask GuardOrphanUpload(CancellationToken token)
        {
            var opening = await Guard(state, token);
            if (coordination.UsesNativeData)
                Require(Value(await client.GetRepositoryAsync(token)).Identity == plan.Repository);
            var current = opening.State!;
            Require(current.Repository == plan.Repository && current.Stage == GitHubCoordinationStage.Claimed
                && current.ContentCommitOid is null && current.ProposalCommitOid is null);
            if (await ReadRef(token) != plan.BeforeOid)
                throw new FailureException(new(GitHubProposalFailureKind.Conflict));
            // Recheck the original caller, current owner/lease and mutation
            // ordinal after that physical ref read. This consumes no send right.
            Require(coordination.ConsumeGuardRead(opening.Guard!, token) is not null);
        }

        foreach (var file in plan.Bytes)
        {
            var oid = plan.BlobOids[file.Key];
            var observed = await client.GetBlobAsync(oid, cancellationToken);
            if (observed.Value is not null)
            {
                Require(ExactBlob(observed.Value, oid, file.Value));
                observedObjects[oid] = observed.Value;
                continue;
            }
            if (observed.Failure?.Code != GitHubFailureCode.NotFound) Throw(observed);
            await GuardOrphanUpload(cancellationToken);
            var mutation = await client.CreateBlobAsync(oid, file.Value.AsMemory(), cancellationToken);
            observedObjects[oid] = await Readback(mutation, token => client.GetBlobAsync(oid, token),
                value => ExactBlob(value, oid, file.Value));
        }
        foreach (var tree in plan.Graph.Trees.OrderByDescending(p => Depth(p.Key)))
        {
            var oid = TreeOid(tree.Value);
            // Preparation authenticated inherited objects; only prospective trees need materialization.
            // Final content verification still rereads the complete graph before success.
            if (plan.BaseTreeOids.Contains(oid)) continue;
            // A prospective tree may not exist yet. GitHub's recursive route
            // returns 422 for that absence, so reserve it for known objects
            // and post-create readback; do not reinterpret validation errors.
            var observed = await client.GetTreeAsync(oid, cancellationToken, allowRecursiveFallback: false);
            if (observed.Value is not null)
            {
                Require(ExactTree(observed.Value, oid, tree.Value));
                observedObjects[oid] = observed.Value;
                continue;
            }
            if (observed.Failure?.Code != GitHubFailureCode.NotFound) Throw(observed);
            await GuardOrphanUpload(cancellationToken);
            var mutation = await client.CreateTreeAsync(oid, tree.Value, cancellationToken);
            observedObjects[oid] = await Readback(mutation, token => client.GetTreeAsync(oid, token), value => ExactTree(value, oid, tree.Value));
        }
        await GuardOrphanUpload(cancellationToken);
        var created = await client.CreateCommitAsync(plan.Commit, cancellationToken);
        var actualCommit = await Readback(created, token => client.GetCommitAsync(plan.Commit.ExpectedOid, token), value => ExactCommit(value, plan.Commit));
        IGitHubProposalContent? verified = null;
        await FinalVerification(created, async token =>
        {
            await VerifyContent(plan, token, actualCommit, observedObjects);
            verified = new ContentCapability(plan);
            await CheckGate(plan, state, allowSuccessor: true, token);
        }, () => verified);
        return new(GitHubProposalOutcome.ContentVerified, Content: new ContentCapability(plan));
    });

    internal ValueTask<GitHubProposalResult> AdvanceRefAsync(IGitHubProposalContent content,
        IGitHubCoordinationStateCapability state, IGitHubProposalRefEntitlement? entitlement = null,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        Require(content is ContentCapability owned && ReferenceEquals(owned.Plan.Owner, this));
        var plan = ((ContentCapability)content).Plan;
        // Consumption precedes all replay/terminal paths. Reading a stage never mints a replacement.
        var mayWrite = entitlement is not null
            && coordination.ConsumeProposalRefEntitlement(entitlement, state, plan.Commit.ExpectedOid, plan.BeforeOid);
        if (entitlement is not null && !mayWrite)
            throw new FailureException(new(GitHubProposalFailureKind.InvalidInput));
        Require(state.Stage is GitHubCoordinationStage.ContentCreated or GitHubCoordinationStage.ProposalRefAdvanced
            && state.ContentCommitOid == plan.Commit.ExpectedOid);
        var head = await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
        await VerifyContent(plan, cancellationToken);
        head = await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
        if (head == plan.Commit.ExpectedOid)
            return new(GitHubProposalOutcome.RefVerified, Content: content);
        if (!mayWrite) throw new FailureException(new(GitHubProposalFailureKind.Unresolved));
        Require(state.Stage == GitHubCoordinationStage.ContentCreated);
        cancellationToken.ThrowIfCancellationRequested();
        var coordinated = await coordination.AdvanceProposalRefsAsync(state, entitlement!, plan.Commit.ExpectedOid,
            plan.Commit.TreeOid, plan.BeforeOid, cancellationToken).ConfigureAwait(false);
        if (coordinated.State is null)
            throw new FailureException(new(coordinated.Failure?.Kind == GitHubCoordinationFailureKind.ObjectMismatch
                ? GitHubProposalFailureKind.Integrity : GitHubProposalFailureKind.Unresolved, coordinated.Failure?.TransportFailure,
                coordinated.Failure?.Delivery ?? GitHubDelivery.NotDispatched, coordinated.Failure?.Context,
                coordinated.Failure?.Permissions, coordinated.Failure?.ReadbackFailure, coordinated.Failure?.Kind));
        await FinalVerification(coordinated.PublicationMutation!, async token =>
        {
            await VerifyContent(plan, token);
            Require(await CheckGate(plan, coordinated.State, allowSuccessor: true, token) == plan.Commit.ExpectedOid);
        }, () => content);
        return new(GitHubProposalOutcome.RefVerified, Content: content, Coordination: coordinated);
    });

    private async ValueTask<GitHubCoordinationResult> Guard(IGitHubCoordinationStateCapability state,
        CancellationToken token, bool readOnly = false)
    {
        Require(state is not null && state.AuthorityCommitmentSha256 == authority.AuthorityCommitmentSha256
            && state.OperationCommitmentSha256 == authority.OperationCommitmentSha256);
        var result = readOnly ? await coordination.ReadResourceAsync(state!, token)
            : await coordination.ReadClaimAsync(state!, token);
        if (result.State is null || !readOnly && result.Guard is null)
            throw new FailureException(new(GitHubProposalFailureKind.Conflict, result.Failure?.TransportFailure,
                result.Failure?.Delivery ?? GitHubDelivery.NotDispatched, result.Failure?.Context,
                result.Failure?.Permissions, result.Failure?.ReadbackFailure, result.Failure?.Kind));
        if (!readOnly) Require(coordination.ValidateGuard(result.Guard!) is not null);
        return result;
    }

    private async ValueTask<string> CheckGate(PreparedCapability plan, IGitHubCoordinationStateCapability state,
        bool allowSuccessor, CancellationToken token)
    {
        var observation = await Guard(state, token, plan.ReadOnly);
        var guarded = observation.State!;
        Require(guarded.Repository == plan.Repository
            && (guarded.ContentCommitOid is null || guarded.ContentCommitOid == plan.Commit.ExpectedOid)
            && (guarded.ProposalCommitOid is null || guarded.ProposalCommitOid == plan.Commit.ExpectedOid)
            && (guarded.ProposalTreeOid is null || guarded.ProposalTreeOid == plan.Commit.TreeOid));
        var basis = Value(await coordination.ReadBaseTreeAsync(token));
        Require(basis.Oid == plan.BaseTreeOid);
        var oid = await ReadRef(token);
        if (plan.ReadOnly) return oid;
        if (oid != plan.BeforeOid && !(allowSuccessor && oid == plan.Commit.ExpectedOid))
            throw new FailureException(new(GitHubProposalFailureKind.Conflict));
        if (mutationPreflight is not null && await mutationPreflight(observation.Guard!, token).ConfigureAwait(false) is { } failure)
            throw new FailureException(failure);
        if (guarded.Stage == GitHubCoordinationStage.ProposalRefAdvanced && oid != plan.Commit.ExpectedOid)
            throw new FailureException(new(GitHubProposalFailureKind.Conflict));
        return oid;
    }

    private async ValueTask<string> ReadRef(CancellationToken token)
    {
        var head = await coordination.ReadDataRefAsync(proposalRef, token);
        if (head.Value is not null) return head.Value.Oid;
        if (head.Failure?.Code != GitHubFailureCode.NotFound) Throw(head);
        return Zero;
    }

    private async ValueTask VerifyContent(PreparedCapability plan, CancellationToken token, GitHubCommit? actualCommit = null,
        IReadOnlyDictionary<string, GitHubValue>? observedObjects = null)
    {
        try
        {
            var failure = await coordination.VerifyExpectedProposalIntegrityAsync(plan.Commit,
                authority.ChangedFiles.Select(file => new GitHubCoordinationChangedFile(file.Path, file.CandidateFileSha256)).ToImmutableArray(),
                actualCommit, token, observedObjects);
            if (failure is { } kind) throw new FailureException(new(kind));
        }
        catch (GitHubCoordinationException exception) when (exception.Failure is { } failure)
        {
            throw new FailureException(new(GitHubProposalFailureKind.Transport, failure.TransportFailure,
                failure.Delivery, failure.Context, failure.Permissions, failure.ReadbackFailure));
        }
    }

    private async ValueTask<Graph> ReadBasis(string rootOid, CancellationToken token)
    {
        try
        {
            var verified = await coordination.ReadVerifiedProposalBasisAsync(rootOid,
                authority.ChangedFiles.Select(file => new GitHubCoordinationChangedFile(file.Path, file.OriginalFileSha256)).ToImmutableArray(), token);
            if (verified.Failure is { } kind) throw new FailureException(new(kind));
            return verified.Graph!;
        }
        catch (GitHubCoordinationException exception) when (exception.Failure is { } failure)
        {
            throw new FailureException(new(GitHubProposalFailureKind.Transport, failure.TransportFailure,
                failure.Delivery, failure.Context, failure.Permissions, failure.ReadbackFailure));
        }
    }

    private async ValueTask CheckBlob(GitHubTreeEntry entry, string expectedHash, CancellationToken token)
    {
        var blob = Value(await coordination.ReadVerifiedProposalBlobAsync(entry.Oid, token));
        Require(ObjectOid("blob", blob.Bytes.AsSpan()) == entry.Oid && Sha256(blob.Bytes.AsSpan()) == expectedHash
            && (entry.Size is null || entry.Size == blob.Bytes.Length));
    }

    private async ValueTask<Graph> ReadGraph(string rootOid, CancellationToken token)
    {
        var trees = new Dictionary<string, ImmutableArray<GitHubTreeEntry>>(StringComparer.Ordinal);
        var files = new Dictionary<string, GitHubTreeEntry>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string Oid)>();
        pending.Push(("", rootOid));
        long metadata = 0;
        var count = 0;
        while (pending.TryPop(out var node))
        {
            if (Depth(node.Path) > 128) throw new FailureException(new(GitHubProposalFailureKind.Bounds));
            var tree = Value(await coordination.ReadVerifiedProposalTreeAsync(node.Oid, token));
            count = checked(count + tree.Entries.Length);
            if (count > 100_000 || trees.Count >= 100_000) throw new FailureException(new(GitHubProposalFailureKind.Bounds));
            Require(TreeOid(tree.Entries) == node.Oid);
            trees.Add(node.Path, tree.Entries);
            foreach (var entry in tree.Entries)
            {
                var path = node.Path.Length == 0 ? entry.Path : node.Path + "/" + entry.Path;
                metadata = checked(metadata + (long)path.Length * 6 + 128);
                if (metadata > 64 * 1024 * 1024) throw new FailureException(new(GitHubProposalFailureKind.Bounds));
                Require(path.Length <= 1024 && names.Add(path));
                if (entry.Mode == GitHubTreeMode.Directory)
                {
                    Require(entry.Size is null);
                    pending.Push((path, entry.Oid));
                }
                else files.Add(path, entry);
            }
        }
        return new(rootOid, trees, files);
    }

    private static Graph Overlay(Graph basis, Dictionary<string, string> replacement)
    {
        var result = new Dictionary<string, ImmutableArray<GitHubTreeEntry>>(StringComparer.Ordinal);
        var oids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in basis.Trees.OrderByDescending(p => Depth(p.Key)))
        {
            var entries = node.Value.Select(entry =>
            {
                var path = node.Key.Length == 0 ? entry.Path : node.Key + "/" + entry.Path;
                return entry.Mode == GitHubTreeMode.Directory ? entry with { Oid = oids[path] }
                    : replacement.TryGetValue(path, out var oid) ? entry with { Oid = oid, Size = null } : entry;
            }).ToImmutableArray();
            oids.Add(node.Key, TreeOid(entries)); // Also bounds every prospective request before the first write.
            result.Add(node.Key, entries);
        }
        return new(oids[""], result, basis.Files);
    }

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;
    private static bool Regular(GitHubTreeMode mode) => mode is GitHubTreeMode.File or GitHubTreeMode.Executable;
    private static bool ExactBlob(GitHubBlob blob, string oid, ImmutableArray<byte> bytes) =>
        blob.Oid == oid && blob.Bytes.AsSpan().SequenceEqual(bytes.AsSpan()) && ObjectOid("blob", blob.Bytes.AsSpan()) == oid;
    private static bool ExactTree(GitHubTree tree, string oid, ImmutableArray<GitHubTreeEntry> entries) =>
        tree.Oid == oid && TreeOid(tree.Entries) == oid && TreeOid(entries) == oid;

    private static T Value<T>(GitHubApiResult<T> result) where T : class
    {
        if (result.Value is null) Throw(result);
        return result.Value!;
    }
    private static void Throw<T>(GitHubApiResult<T> result) where T : class =>
        throw new FailureException(new(GitHubProposalFailureKind.Transport, result.Failure, result.Delivery,
            result.Context, result.RequiredPermissions));

    private async ValueTask<TRead> Readback<TMutation, TRead>(GitHubApiResult<TMutation> mutation,
        Func<CancellationToken, ValueTask<GitHubApiResult<TRead>>> read, Func<TRead, bool> exact)
        where TMutation : class where TRead : class
    {
        if (mutation.Delivery == GitHubDelivery.NotDispatched) Throw(mutation);
        using var recovery = client.CreateRecoveryScope(TimeSpan.FromSeconds(30));
        var observed = await GitHubObjectReadback.ObserveAsync(read, recovery.Token);
        bool matches;
        try
        {
            matches = observed.Value is not null && exact(observed.Value)
                && (mutation.Value is not TRead direct || exact(direct));
        }
        catch (GitHubProposalException) { matches = false; }
        if (!matches)
            throw new FailureException(new(observed.Value is null ? GitHubProposalFailureKind.Unresolved : GitHubProposalFailureKind.Integrity,
                mutation.Failure, mutation.Delivery, mutation.Context, mutation.RequiredPermissions,
                RecoveryFailure(observed.Failure, recovery)));
        return observed.Value!;
    }

    private static GitHubFailure? RecoveryFailure(GitHubFailure? failure, CancellationTokenSource recovery) =>
        failure?.Code == GitHubFailureCode.Cancelled && recovery.IsCancellationRequested
            ? failure with { Code = GitHubFailureCode.Timeout } : failure;

    private async ValueTask FinalVerification<T>(GitHubApiResult<T> mutation,
        Func<CancellationToken, ValueTask> verify, Func<IGitHubProposalContent?> residual) where T : class
    {
        using var recovery = client.CreateRecoveryScope(TimeSpan.FromSeconds(30));
        try { await verify(recovery.Token); }
        catch (FailureException failure)
        {
            throw new FailureException(new(failure.Failure.Kind, mutation.Failure, mutation.Delivery,
                mutation.Context, mutation.RequiredPermissions,
                RecoveryFailure(failure.Failure.Readback ?? failure.Failure.Transport, recovery),
                failure.Failure.CoordinationCause), residual());
        }
        catch (Exception failure)
        {
            var integrity = failure is GitHubProposalException or ArgumentException;
            throw new FailureException(new(integrity ? GitHubProposalFailureKind.Integrity : GitHubProposalFailureKind.Transport,
                mutation.Failure, mutation.Delivery, mutation.Context, mutation.RequiredPermissions,
                failure is OperationCanceledException ? RecoveryFailure(client.CancellationFailure, recovery)
                    : new(integrity ? GitHubFailureCode.InvalidResponse : GitHubFailureCode.HostFailure)), residual());
        }
    }

    private async ValueTask<GitHubProposalResult> Run(Func<ValueTask<GitHubProposalResult>> action)
    {
        try { return await action(); }
        catch (FailureException failure) { return new(GitHubProposalOutcome.Failed, Content: failure.Content, Failure: failure.Failure); }
        catch (OperationCanceledException) { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.Transport, client.CancellationFailure)); }
        catch (GitHubProposalException) { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.Integrity)); }
        catch { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.InvalidInput)); }
    }

    private sealed class FailureException(GitHubProposalFailure failure, IGitHubProposalContent? content = null) : Exception("Proposal publication failed.")
    {
        internal GitHubProposalFailure Failure { get; } = failure;
        internal IGitHubProposalContent? Content { get; } = content;
    }
    private sealed class PreparedCapability(GitHubProposalStore owner, GitHubRepositoryIdentity repository,
        string baseTreeOid, string beforeOid, GitHubCreateCommit commit, Graph graph,
        Dictionary<string, ImmutableArray<byte>> bytes, Dictionary<string, string> blobOids,
        HashSet<string> baseTreeOids, bool readOnly) : IGitHubPreparedProposal
    {
        internal GitHubProposalStore Owner { get; } = owner;
        internal GitHubRepositoryIdentity Repository { get; } = repository;
        internal string BaseTreeOid { get; } = baseTreeOid;
        internal string BeforeOid { get; } = beforeOid;
        internal GitHubCreateCommit Commit { get; } = commit;
        internal Graph Graph { get; } = graph;
        internal Dictionary<string, ImmutableArray<byte>> Bytes { get; } = bytes;
        internal Dictionary<string, string> BlobOids { get; } = blobOids;
        internal HashSet<string> BaseTreeOids { get; } = baseTreeOids;
        internal bool ReadOnly { get; } = readOnly;
        public override string ToString() => nameof(PreparedCapability);
    }
    private sealed class ContentCapability(PreparedCapability plan) : IGitHubProposalContent
    {
        internal PreparedCapability Plan { get; } = plan;
        public string CommitOid => Plan.Commit.ExpectedOid;
        public string TreeOid => Plan.Commit.TreeOid;
        public string Ref => Plan.Owner.proposalRef;
        public override string ToString() => nameof(ContentCapability);
    }

    private sealed class Inspection(GitHubProposalStore owner, string? contextHead, string reference,
        string commit, string tree, string before, string observed, bool exists) : IGitHubInspectedProposal
    {
        internal GitHubProposalStore Owner { get; } = owner;
        internal string? ContextHead { get; } = contextHead;
        public string Ref { get; } = reference;
        public string CommitOid { get; } = commit;
        public string TreeOid { get; } = tree;
        public string BeforeOid { get; } = before;
        public string ObservedRefOid { get; } = observed;
        public bool ContentExists { get; } = exists;
        public override string ToString() => nameof(Inspection);
    }
}
