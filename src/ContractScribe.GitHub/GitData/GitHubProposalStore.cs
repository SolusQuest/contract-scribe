using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using static ContractScribe.GitHub.GitData.GitHubProposalObjects;

namespace ContractScribe.GitHub.GitData;

internal interface IGitHubPreparedProposal;
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
    GitHubProposalFailure? Failure = null, GitHubProposalObservation? Observation = null) : GitHubValue;

internal sealed class GitHubProposalStore
{
    private readonly GitHubApiClient client;
    private readonly GitHubCoordinationStore coordination;
    private readonly ValidatedGitHubPublicationAuthority authority;
    private readonly string proposalRef;
    private readonly string generationKey;
    private const string Zero = GitHubPublicationContract.MissingGitObjectId;

    private GitHubProposalStore(GitHubApiClient client, GitHubCoordinationStore coordination)
    {
        this.client = client;
        this.coordination = coordination;
        authority = client.Authority;
        proposalRef = GitHubPublicationFactory.CreateProposalRef(authority);
        generationKey = proposalRef[(proposalRef.LastIndexOf('/') + 1)..];
    }

    internal static GitHubProposalStore Create(GitHubApiClient client, GitHubCoordinationStore coordination) => new(client, coordination);

    internal ValueTask<GitHubProposalResult> PrepareAsync(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, CancellationToken cancellationToken = default) =>
        Run(() => PrepareCore(state, payload, readOnly: false, cancellationToken));

    private async ValueTask<GitHubProposalResult> PrepareCore(IGitHubCoordinationStateCapability state,
        ValidatedGitHubChangedFilePayload payload, bool readOnly, CancellationToken cancellationToken)
    {
        Require(payload is not null && payload.AuthorityCommitmentSha256 == authority.AuthorityCommitmentSha256
            && payload.Files.Length == authority.ChangedFiles.Length);
        long total = 0;
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var file in payload!.Files)
        {
            Require(!file.CandidateBytes.IsDefault && file.CandidateBytes.Length <= GitHubPublicationContract.MaximumPayloadBytesPerFile);
            total = checked(total + file.CandidateBytes.Length);
            Require(total <= GitHubPublicationContract.MaximumAggregatePayloadBytes && bytes.TryAdd(file.Path, file.CandidateBytes));
        }
        foreach (var file in authority.ChangedFiles)
            Require(bytes.TryGetValue(file.Path, out var value) && Sha256(value.AsSpan()) == file.CandidateFileSha256);

        var guarded = await Guard(state, cancellationToken, readOnly);
        var baseCommit = Value(await client.GetCommitAsync(authority.ExpectedBaseCommitOid, cancellationToken));
        var basis = await ReadGraph(baseCommit.TreeOid, cancellationToken);
        foreach (var file in authority.ChangedFiles)
        {
            Require(basis.Files.TryGetValue(file.Path, out var entry) && Regular(entry.Mode));
            await CheckBlob(entry!, file.OriginalFileSha256, cancellationToken);
        }

        var before = Zero;
        if (authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
        {
            var preceding = coordination.AdmissionSource(guarded);
            Require(preceding is not null && preceding.Stage == GitHubCoordinationStage.Published
                && preceding.OperationId == authority.PrecedingOperationId
                && preceding.AuthorityCommitmentSha256 == authority.PrecedingAuthorityCommitmentSha256
                && preceding.CurrentCandidateCommitmentSha256 == authority.PrecedingCandidateCommitmentSha256
                && preceding.GenerationId == authority.GenerationId && preceding.TargetCommitOid == authority.ExpectedBaseCommitOid);
            before = preceding!.ProposalCommitOid!;
            var previousCommit = Value(await client.GetCommitAsync(before, cancellationToken));
            Require(previousCommit.Parents.Length == 1 && previousCommit.TreeOid == preceding.ProposalTreeOid);
            var expectedParent = preceding.TargetCommitOid;
            if (preceding.Transition == "same-snapshot-append")
            {
                var parentSource = coordination.AdmissionSource(preceding);
                Require(parentSource is not null && parentSource.Stage == GitHubCoordinationStage.Published
                    && parentSource.OperationId == preceding.PrecedingOperationId
                    && parentSource.GenerationId == preceding.GenerationId
                    && parentSource.TargetCommitOid == preceding.TargetCommitOid);
                expectedParent = parentSource!.ProposalCommitOid!;
            }
            Require(ExactCommit(previousCommit, Commit(previousCommit.TreeOid, expectedParent,
                preceding.OperationCommitmentSha256, generationKey)));
            var previous = await ReadGraph(previousCommit.TreeOid, cancellationToken);
            var replacement = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in authority.PrecedingChangedFiles)
            {
                Require(basis.Files.TryGetValue(file.Path, out var original) && Regular(original.Mode)
                    && previous.Files.TryGetValue(file.Path, out var published) && published.Mode == original.Mode);
                var publishedEntry = previous.Files[file.Path];
                await CheckBlob(publishedEntry, file.CandidateFileSha256, cancellationToken);
                replacement.Add(file.Path, publishedEntry.Oid);
            }
            Require(Overlay(basis, replacement).RootOid == previous.RootOid);
        }

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
        await VerifyContent(plan, Value(await client.GetCommitAsync(plan.Commit.ExpectedOid, cancellationToken)), cancellationToken);
        var current = await Guard(state!, cancellationToken, readOnly: true);
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
        var existing = await client.GetCommitAsync(plan.Commit.ExpectedOid, cancellationToken);
        if (existing.Value is not null)
        {
            await VerifyContent(plan, existing.Value, cancellationToken);
            await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
            return new(GitHubProposalOutcome.ContentVerified, Content: new ContentCapability(plan));
        }
        if (existing.Failure?.Code != GitHubFailureCode.NotFound) Throw(existing);
        Require(state.Stage == GitHubCoordinationStage.Claimed && state.ContentCommitOid is null);
        foreach (var file in plan.Bytes)
        {
            var oid = plan.BlobOids[file.Key];
            var observed = await client.GetBlobAsync(oid, cancellationToken);
            if (observed.Value is not null)
            {
                Require(ExactBlob(observed.Value, oid, file.Value));
                continue;
            }
            if (observed.Failure?.Code != GitHubFailureCode.NotFound) Throw(observed);
            await CheckGate(plan, state, allowSuccessor: false, cancellationToken);
            var mutation = await client.CreateBlobAsync(oid, file.Value.AsMemory(), cancellationToken);
            await Readback(mutation, token => client.GetBlobAsync(oid, token),
                value => ExactBlob(value, oid, file.Value));
        }
        foreach (var tree in plan.Graph.Trees.OrderByDescending(p => Depth(p.Key)))
        {
            var oid = TreeOid(tree.Value);
            // Preparation authenticated inherited objects; only prospective trees need materialization.
            // Final content verification still rereads the complete graph before success.
            if (plan.BaseTreeOids.Contains(oid)) continue;
            var observed = await client.GetTreeAsync(oid, cancellationToken);
            if (observed.Value is not null)
            {
                Require(ExactTree(observed.Value, oid, tree.Value));
                continue;
            }
            if (observed.Failure?.Code != GitHubFailureCode.NotFound) Throw(observed);
            await CheckGate(plan, state, allowSuccessor: false, cancellationToken);
            var mutation = await client.CreateTreeAsync(oid, tree.Value, cancellationToken);
            await Readback(mutation, token => client.GetTreeAsync(oid, token), value => ExactTree(value, oid, tree.Value));
        }
        await CheckGate(plan, state, allowSuccessor: false, cancellationToken);
        var created = await client.CreateCommitAsync(plan.Commit, cancellationToken);
        await Readback(created, token => client.GetCommitAsync(plan.Commit.ExpectedOid, token), value => ExactCommit(value, plan.Commit));
        IGitHubProposalContent? verified = null;
        await FinalVerification(created, async token =>
        {
            await VerifyContent(plan, Value(await client.GetCommitAsync(plan.Commit.ExpectedOid, token)), token);
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
        await VerifyContent(plan, Value(await client.GetCommitAsync(plan.Commit.ExpectedOid, cancellationToken)), cancellationToken);
        head = await CheckGate(plan, state, allowSuccessor: true, cancellationToken);
        if (head == plan.Commit.ExpectedOid)
            return new(GitHubProposalOutcome.RefVerified, Content: content);
        if (!mayWrite) throw new FailureException(new(GitHubProposalFailureKind.Unresolved));
        Require(state.Stage == GitHubCoordinationStage.ContentCreated);
        cancellationToken.ThrowIfCancellationRequested();
        var mutation = await client.UpdateRefAsync(new(proposalRef, plan.BeforeOid,
            plan.Commit.ExpectedOid, plan.BeforeOid == Zero), cancellationToken);
        await Readback(mutation, token => client.GetRefAsync(proposalRef, token),
            value => value.Oid == plan.Commit.ExpectedOid && value.Name == proposalRef);
        IGitHubProposalContent? verified = null;
        await FinalVerification(mutation, async token =>
        {
            await VerifyContent(plan, Value(await client.GetCommitAsync(plan.Commit.ExpectedOid, token)), token);
            verified = new ContentCapability(plan);
            Require(await CheckGate(plan, state, allowSuccessor: true, token) == plan.Commit.ExpectedOid);
        }, () => verified);
        return new(GitHubProposalOutcome.RefVerified, Content: content);
    });

    private async ValueTask<IGitHubCoordinationStateCapability> Guard(IGitHubCoordinationStateCapability state,
        CancellationToken token, bool readOnly = false)
    {
        Require(state is not null && state.AuthorityCommitmentSha256 == authority.AuthorityCommitmentSha256
            && state.OperationCommitmentSha256 == authority.OperationCommitmentSha256);
        var repository = Value(await client.GetRepositoryAsync(token));
        var result = readOnly ? await coordination.ReadResourceAsync(state!, token)
            : await coordination.ReadClaimAsync(state!, token);
        if (result.State is null || !readOnly && result.Guard is null)
            throw new FailureException(new(GitHubProposalFailureKind.Conflict, result.Failure?.TransportFailure,
                result.Failure?.Delivery ?? GitHubDelivery.NotDispatched, result.Failure?.Context,
                result.Failure?.Permissions, result.Failure?.ReadbackFailure, result.Failure?.Kind));
        Require(repository.Identity == result.State.Repository);
        if (!readOnly) Require(coordination.ValidateGuard(result.Guard!) is not null);
        return result.State;
    }

    private async ValueTask<string> CheckGate(PreparedCapability plan, IGitHubCoordinationStateCapability state,
        bool allowSuccessor, CancellationToken token)
    {
        var guarded = await Guard(state, token, plan.ReadOnly);
        Require(guarded.Repository == plan.Repository
            && (guarded.ContentCommitOid is null || guarded.ContentCommitOid == plan.Commit.ExpectedOid)
            && (guarded.ProposalCommitOid is null || guarded.ProposalCommitOid == plan.Commit.ExpectedOid)
            && (guarded.ProposalTreeOid is null || guarded.ProposalTreeOid == plan.Commit.TreeOid));
        var basis = Value(await client.GetCommitAsync(authority.ExpectedBaseCommitOid, token));
        Require(basis.TreeOid == plan.BaseTreeOid);
        var oid = await ReadRef(token);
        if (plan.ReadOnly) return oid;
        if (oid != plan.BeforeOid && !(allowSuccessor && oid == plan.Commit.ExpectedOid))
            throw new FailureException(new(GitHubProposalFailureKind.Conflict));
        if (guarded.Stage == GitHubCoordinationStage.ProposalRefAdvanced && oid != plan.Commit.ExpectedOid)
            throw new FailureException(new(GitHubProposalFailureKind.Conflict));
        return oid;
    }

    private async ValueTask<string> ReadRef(CancellationToken token)
    {
        var head = await client.GetRefAsync(proposalRef, token);
        if (head.Value is not null) return head.Value.Oid;
        if (head.Failure?.Code != GitHubFailureCode.NotFound) Throw(head);
        return Zero;
    }

    private async ValueTask VerifyContent(PreparedCapability plan, GitHubCommit commit, CancellationToken token)
    {
        Require(ExactCommit(commit, plan.Commit));
        var graph = await ReadGraph(commit.TreeOid, token);
        Require(graph.RootOid == plan.Graph.RootOid);
        foreach (var file in authority.ChangedFiles)
        {
            Require(graph.Files.TryGetValue(file.Path, out var entry) && Regular(entry.Mode));
            await CheckBlob(entry!, file.CandidateFileSha256, token);
        }
    }

    private async ValueTask CheckBlob(GitHubTreeEntry entry, string expectedHash, CancellationToken token)
    {
        var blob = Value(await client.GetBlobAsync(entry.Oid, token));
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
            var tree = Value(await client.GetTreeAsync(node.Oid, token));
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

    private static async ValueTask Readback<TMutation, TRead>(GitHubApiResult<TMutation> mutation,
        Func<CancellationToken, ValueTask<GitHubApiResult<TRead>>> read, Func<TRead, bool> exact)
        where TMutation : class where TRead : class
    {
        if (mutation.Delivery == GitHubDelivery.NotDispatched) Throw(mutation);
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observed = await read(recovery.Token);
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
                recovery.IsCancellationRequested ? new(GitHubFailureCode.Timeout) : observed.Failure));
    }

    private static async ValueTask FinalVerification<T>(GitHubApiResult<T> mutation,
        Func<CancellationToken, ValueTask> verify, Func<IGitHubProposalContent?> residual) where T : class
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await verify(recovery.Token); }
        catch (FailureException failure)
        {
            throw new FailureException(new(failure.Failure.Kind, mutation.Failure, mutation.Delivery,
                mutation.Context, mutation.RequiredPermissions,
                recovery.IsCancellationRequested ? new(GitHubFailureCode.Timeout)
                    : failure.Failure.Readback ?? failure.Failure.Transport,
                failure.Failure.CoordinationCause), residual());
        }
        catch (Exception failure)
        {
            var integrity = failure is GitHubProposalException or ArgumentException;
            throw new FailureException(new(integrity ? GitHubProposalFailureKind.Integrity : GitHubProposalFailureKind.Transport,
                mutation.Failure, mutation.Delivery, mutation.Context, mutation.RequiredPermissions,
                new(recovery.IsCancellationRequested ? GitHubFailureCode.Timeout
                    : integrity ? GitHubFailureCode.InvalidResponse : GitHubFailureCode.HostFailure)), residual());
        }
    }

    private static async ValueTask<GitHubProposalResult> Run(Func<ValueTask<GitHubProposalResult>> action)
    {
        try { return await action(); }
        catch (FailureException failure) { return new(GitHubProposalOutcome.Failed, Content: failure.Content, Failure: failure.Failure); }
        catch (OperationCanceledException) { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.Transport, new(GitHubFailureCode.Cancelled))); }
        catch (GitHubProposalException) { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.Integrity)); }
        catch { return new(GitHubProposalOutcome.Failed, Failure: new(GitHubProposalFailureKind.InvalidInput)); }
    }

    private sealed class FailureException(GitHubProposalFailure failure, IGitHubProposalContent? content = null) : Exception("Proposal publication failed.")
    {
        internal GitHubProposalFailure Failure { get; } = failure;
        internal IGitHubProposalContent? Content { get; } = content;
    }
    private sealed record Graph(string RootOid, Dictionary<string, ImmutableArray<GitHubTreeEntry>> Trees,
        Dictionary<string, GitHubTreeEntry> Files);
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
}
