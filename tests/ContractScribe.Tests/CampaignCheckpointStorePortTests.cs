using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class CampaignCheckpointStorePortTests
{
    [Fact]
    public async Task Exact_replace_and_readback_are_required_for_acceptance()
    {
        var predecessor = CreateOpenArtifact();
        var transition = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var store = new MemoryStore(predecessor);

        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(
            store,
            transition);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(0, store.CreateCalls);
        Assert.True(store.Artifact!.ExactUtf8Json.AsSpan().SequenceEqual(
            transition.Artifact.ExactUtf8Json.AsSpan()));
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public async Task Exact_replay_and_lost_ack_perform_no_write_but_still_read_back()
    {
        var predecessor = CreateOpenArtifact();
        var applied = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Timeout);
        var replay = CampaignStateReducer.Stop(applied.Artifact, CampaignTerminalKind.Timeout);
        Assert.Equal(CampaignTransitionKind.Unchanged, replay.Kind);
        var store = new MemoryStore(applied.Artifact);

        var unchanged = await CampaignCheckpointAcceptance.AcceptAsync(
            store,
            replay);
        var lostAck = await CampaignCheckpointAcceptance.AcceptAsync(
            store,
            applied);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, unchanged.Kind);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, lostAck.Kind);
        Assert.Equal(0, store.CreateCalls);
        Assert.Equal(0, store.ReplaceCalls);
        Assert.Equal(4, store.ReadCalls);
    }

    [Fact]
    public async Task Stale_writer_invalid_readback_and_invalid_transition_never_fall_back()
    {
        var predecessor = CreateOpenArtifact();
        var intended = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled);
        var competing = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Timeout);
        var staleStore = new MemoryStore(competing.Artifact);

        var stale = await CampaignCheckpointAcceptance.AcceptAsync(
            staleStore,
            intended);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, stale.Kind);
        Assert.Equal(0, staleStore.CreateCalls);
        Assert.Equal(0, staleStore.ReplaceCalls);

        var badReadbackStore = new MemoryStore(predecessor) { CorruptAfterWrite = true };
        var badReadback = await CampaignCheckpointAcceptance.AcceptAsync(
            badReadbackStore,
            intended);
        Assert.Equal(CampaignCheckpointAcceptanceKind.ReadbackMismatch, badReadback.Kind);
        Assert.Equal(1, badReadbackStore.ReplaceCalls);
        Assert.Equal(0, badReadbackStore.CreateCalls);

        var invalid = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Complete);
        Assert.Equal(CampaignTransitionKind.Rejected, invalid.Kind);
        var untouched = new MemoryStore(predecessor);
        var invalidAcceptance = await CampaignCheckpointAcceptance.AcceptAsync(
            untouched,
            invalid);
        Assert.Equal(CampaignCheckpointAcceptanceKind.InvalidTransition, invalidAcceptance.Kind);
        Assert.Equal(0, untouched.ReadCalls);
        Assert.Equal(0, untouched.CreateCalls);
        Assert.Equal(0, untouched.ReplaceCalls);
    }

    [Fact]
    public async Task Non_initial_transition_cannot_create_an_absent_checkpoint()
    {
        var initial = CreateOpenArtifact();
        var transition = CampaignStateReducer.Stop(initial, CampaignTerminalKind.Cancelled);
        var store = new MemoryStore(null) { CreateResult = CampaignCheckpointWriteKind.AlreadyPresent };

        var result = await CampaignCheckpointAcceptance.AcceptAsync(
            store,
            transition);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, result.Kind);
        Assert.Equal(0, store.CreateCalls);
        Assert.Equal(0, store.ReplaceCalls);
    }

    [Fact]
    public async Task Initial_create_requires_explicit_initial_authority_and_exact_readback()
    {
        var initial = CreateInitialArtifact();
        var authority = CampaignCheckpointAcceptance.CreateInitialAuthority(initial);
        var store = new MemoryStore(null);

        var result = await CampaignCheckpointAcceptance.AcceptInitialAsync(store, authority);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        Assert.NotNull(result.AcceptedCheckpoint);
        Assert.Equal(1, store.CreateCalls);
        Assert.Equal(0, store.ReplaceCalls);
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public async Task Initial_create_never_adopts_an_exact_concurrent_winner()
    {
        var initial = CreateInitialArtifact();
        var authority = CampaignCheckpointAcceptance.CreateInitialAuthority(initial);
        var alreadyPresent = new MemoryStore(initial);
        var concurrentWinner = new MemoryStore(null)
        {
            CreateResult = CampaignCheckpointWriteKind.AlreadyPresent,
            WinnerOnRejectedWrite = initial,
        };

        var observedBeforeCreate = await CampaignCheckpointAcceptance.AcceptInitialAsync(
            alreadyPresent,
            authority);
        var lostCreate = await CampaignCheckpointAcceptance.AcceptInitialAsync(
            concurrentWinner,
            authority);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, observedBeforeCreate.Kind);
        Assert.Equal(0, alreadyPresent.CreateCalls);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Conflict, lostCreate.Kind);
        Assert.Equal(1, concurrentWinner.CreateCalls);
        Assert.Null(lostCreate.AcceptedCheckpoint);
    }

    [Fact]
    public async Task Conditional_conflict_accepts_only_an_exact_concurrent_winner()
    {
        var predecessor = CreateOpenArtifact();
        var transition = CampaignStateReducer.Stop(predecessor, CampaignTerminalKind.Cancelled);
        var store = new MemoryStore(predecessor)
        {
            ReplaceResult = CampaignCheckpointWriteKind.CurrentMismatch,
            WinnerOnRejectedWrite = transition.Artifact,
        };

        var result = await CampaignCheckpointAcceptance.AcceptAsync(store, transition);

        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public void Oversized_found_read_is_rejected_before_materialization()
    {
        var bytes = new byte[CampaignStateContract.MaximumArtifactUtf8Bytes + 1];

        var result = CampaignCheckpointReadResult.Found(bytes, 0, new string('a', 64));

        Assert.Equal(CampaignCheckpointReadKind.Invalid, result.Kind);
        Assert.True(result.ExactUtf8Json.IsDefault);
    }

    private static CampaignCheckpointArtifact CreateOpenArtifact()
    {
        var path = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "campaign", "state", "empty-terminal.json"));
        var parsed = CampaignStateJson.Parse(File.ReadAllBytes(path));
        var fixture = Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
        var state = fixture.State;
        return CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(
            state.ProductRevision,
            state.CampaignLineage,
            state.Snapshot,
            state.CheckpointRevision,
            state.ConfiguredCeilings,
            state.LineageCharges,
            state.WorkItems,
            terminalOutcome: null));
    }

    private static CampaignCheckpointArtifact CreateInitialArtifact()
    {
        var path = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "campaign", "state", "empty-terminal.json"));
        return Assert.IsType<CampaignCheckpointArtifact>(
            CampaignStateJson.Parse(File.ReadAllBytes(path)).Artifact);
    }

    private sealed class MemoryStore : ICampaignCheckpointStore
    {
        public MemoryStore(CampaignCheckpointArtifact? artifact) => Artifact = artifact;

        public CampaignCheckpointArtifact? Artifact { get; private set; }
        public CampaignCheckpointWriteKind CreateResult { get; init; } = CampaignCheckpointWriteKind.Written;
        public CampaignCheckpointWriteKind ReplaceResult { get; init; } = CampaignCheckpointWriteKind.Written;
        public CampaignCheckpointArtifact? WinnerOnRejectedWrite { get; init; }
        public bool CorruptAfterWrite { get; init; }
        public int ReadCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int ReplaceCalls { get; private set; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            if (Artifact is null)
            {
                return ValueTask.FromResult(CampaignCheckpointReadResult.NotFound());
            }

            if (CorruptAfterWrite && ReplaceCalls + CreateCalls > 0)
            {
                return ValueTask.FromResult(CampaignCheckpointReadResult.Found(
                    Artifact.ExactUtf8Json.AsSpan(),
                    Artifact.CheckpointRevision,
                    new string('0', 64)));
            }

            return ValueTask.FromResult(CampaignCheckpointReadResult.Found(
                Artifact.ExactUtf8Json.AsSpan(),
                Artifact.CheckpointRevision,
                Artifact.Sha256));
        }

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            if (CreateResult != CampaignCheckpointWriteKind.Written || Artifact is not null)
            {
                Artifact = WinnerOnRejectedWrite ?? Artifact;
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    Artifact is not null ? CampaignCheckpointWriteKind.AlreadyPresent : CreateResult));
            }

            Artifact = Assert.IsType<CampaignCheckpointArtifact>(
                CampaignStateJson.Parse(exactUtf8Json).Artifact);
            return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
        }

        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
            long expectedCheckpointRevision,
            string expectedSha256,
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCalls++;
            if (ReplaceResult != CampaignCheckpointWriteKind.Written)
            {
                Artifact = WinnerOnRejectedWrite ?? Artifact;
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(ReplaceResult));
            }

            if (Artifact is null)
            {
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    CampaignCheckpointWriteKind.PredecessorMissing));
            }

            if (Artifact.CheckpointRevision != expectedCheckpointRevision
                || !string.Equals(Artifact.Sha256, expectedSha256, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    CampaignCheckpointWriteKind.CurrentMismatch));
            }

            Artifact = Assert.IsType<CampaignCheckpointArtifact>(
                CampaignStateJson.Parse(exactUtf8Json).Artifact);
            return ValueTask.FromResult(new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.Written));
        }
    }
}
