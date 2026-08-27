using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ContractScribe.Core;

public enum CampaignCheckpointReadKind
{
    NotFound,
    Found,
    Invalid,
    Unreadable,
}

public sealed class CampaignCheckpointReadResult
{
    private CampaignCheckpointReadResult(
        CampaignCheckpointReadKind kind,
        ImmutableArray<byte> exactUtf8Json,
        long? checkpointRevision,
        string? sha256)
    {
        Kind = kind;
        ExactUtf8Json = exactUtf8Json;
        CheckpointRevision = checkpointRevision;
        Sha256 = sha256;
    }

    public CampaignCheckpointReadKind Kind { get; }
    public ImmutableArray<byte> ExactUtf8Json { get; }
    public long? CheckpointRevision { get; }
    public string? Sha256 { get; }

    public static CampaignCheckpointReadResult NotFound() =>
        new(CampaignCheckpointReadKind.NotFound, default, null, null);

    public static CampaignCheckpointReadResult Found(
        ReadOnlySpan<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256)
    {
        if (exactUtf8Json.Length > CampaignStateContract.MaximumArtifactUtf8Bytes
            || checkpointRevision < 0
            || checkpointRevision > CampaignStateContract.MaximumObservation
            || sha256 is null
            || sha256.Length != 64
            || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return Invalid();
        }

        return new(
            CampaignCheckpointReadKind.Found,
            ImmutableArray.CreateRange(exactUtf8Json.ToArray()),
            checkpointRevision,
            sha256);
    }

    public static CampaignCheckpointReadResult Invalid() =>
        new(CampaignCheckpointReadKind.Invalid, default, null, null);

    public static CampaignCheckpointReadResult Unreadable() =>
        new(CampaignCheckpointReadKind.Unreadable, default, null, null);
}

public enum CampaignCheckpointWriteKind
{
    Written,
    AlreadyPresent,
    PredecessorMissing,
    CurrentMismatch,
    Unwritable,
}

public readonly record struct CampaignCheckpointWriteResult(CampaignCheckpointWriteKind Kind);

public interface ICampaignCheckpointStore
{
    ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken);

    ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken);

    ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
        long expectedCheckpointRevision,
        string expectedSha256,
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken);
}

public enum CampaignCheckpointAcceptanceKind
{
    Accepted,
    Conflict,
    InvalidRead,
    Unreadable,
    WriteRejected,
    ReadbackMismatch,
    InvalidTransition,
    Cancelled,
}

public sealed class CampaignAcceptedCheckpoint
{
    private readonly CampaignReservationLifecycleAuthority? _reservationLifecycle;
    private int _invocationGrant;

    internal CampaignAcceptedCheckpoint(
        CampaignCheckpointArtifact artifact,
        CampaignAcceptedCheckpointAuthorityKind authorityKind)
    {
        Artifact = artifact;
        _invocationGrant = authorityKind == CampaignAcceptedCheckpointAuthorityKind.Writer ? 1 : 0;
        _reservationLifecycle = authorityKind == CampaignAcceptedCheckpointAuthorityKind.Observer
            ? null
            : new CampaignReservationLifecycleAuthority();
    }

    public CampaignCheckpointArtifact Artifact { get; }

    internal CampaignReservationLifecycleAuthority ReservationLifecycle =>
        _reservationLifecycle ?? throw new InvalidOperationException("The checkpoint is observation-only.");

    internal bool TryIssueInvocation() =>
        _reservationLifecycle?.IsAvailable == true
        && Interlocked.CompareExchange(ref _invocationGrant, 0, 1) == 1;

    internal bool TryRetireReservation() => _reservationLifecycle?.TryRetire() == true;

    public override string ToString() => nameof(CampaignAcceptedCheckpoint);
}

internal enum CampaignAcceptedCheckpointAuthorityKind
{
    Observer,
    RetirementOnly,
    Writer,
}

internal sealed class CampaignReservationLifecycleAuthority
{
    private const int Available = 0;
    private const int DispatchStarted = 1;
    private const int Retired = 2;
    private int _state;

    internal bool IsAvailable => Volatile.Read(ref _state) == Available;
    internal bool IsDispatchStarted => Volatile.Read(ref _state) == DispatchStarted;

    internal bool TryBeginDispatch() =>
        Interlocked.CompareExchange(ref _state, DispatchStarted, Available) == Available;

    internal bool TryRetire() =>
        Interlocked.CompareExchange(ref _state, Retired, Available) == Available;
}

public sealed class CampaignInitialCheckpointAuthority
{
    internal CampaignInitialCheckpointAuthority(CampaignCheckpointArtifact artifact) => Artifact = artifact;

    internal CampaignCheckpointArtifact Artifact { get; }

    public override string ToString() => nameof(CampaignInitialCheckpointAuthority);
}

public sealed class CampaignCheckpointAcceptanceResult
{
    internal CampaignCheckpointAcceptanceResult(
        CampaignCheckpointAcceptanceKind kind,
        CampaignAcceptedCheckpoint? acceptedCheckpoint)
    {
        Kind = kind;
        AcceptedCheckpoint = acceptedCheckpoint;
    }

    public CampaignCheckpointAcceptanceKind Kind { get; }
    public CampaignAcceptedCheckpoint? AcceptedCheckpoint { get; }
    public CampaignCheckpointArtifact? Artifact => AcceptedCheckpoint?.Artifact;
}

/// <summary>
/// Applies a pure reducer result through a conditional store and accepts it
/// only after exact canonical readback.
/// </summary>
public static class CampaignCheckpointAcceptance
{
    public static async ValueTask<CampaignCheckpointAcceptanceResult> AcceptCurrentAsync(
        ICampaignCheckpointStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var read = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read.Kind != CampaignCheckpointReadKind.Found
                || read.CheckpointRevision is null
                || read.Sha256 is null
                || read.ExactUtf8Json.IsDefault)
            {
                return ReadFailure(read);
            }

            var bytes = read.ExactUtf8Json.AsSpan();
            var recomputed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var parsed = CampaignStateJson.Parse(read.ExactUtf8Json.AsMemory());
            if (!string.Equals(recomputed, read.Sha256, StringComparison.Ordinal)
                || !parsed.IsValid
                || parsed.Artifact is null
                || parsed.Artifact.CheckpointRevision != read.CheckpointRevision
                || !bytes.SequenceEqual(parsed.Artifact.ExactUtf8Json.AsSpan()))
            {
                return new CampaignCheckpointAcceptanceResult(
                    CampaignCheckpointAcceptanceKind.InvalidRead,
                    null);
            }

            return new CampaignCheckpointAcceptanceResult(
                CampaignCheckpointAcceptanceKind.Accepted,
                new CampaignAcceptedCheckpoint(
                    parsed.Artifact,
                    CampaignAcceptedCheckpointAuthorityKind.RetirementOnly));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Cancelled, null);
        }
    }

    public static CampaignInitialCheckpointAuthority CreateInitialAuthority(
        CampaignCheckpointArtifact initialArtifact)
    {
        ArgumentNullException.ThrowIfNull(initialArtifact);
        if (!IsExactInitialArtifact(initialArtifact))
        {
            throw new ArgumentException("The artifact is not an exact initial checkpoint.", nameof(initialArtifact));
        }

        return new CampaignInitialCheckpointAuthority(initialArtifact);
    }

    internal static bool IsExactInitialArtifact(CampaignCheckpointArtifact artifact)
    {
        var state = artifact.State;
        return state.CheckpointRevision == 0
            && state.ActiveReservation is null
            && state.CandidateObservation is null
            && state.CumulativeOutcome is null
            && state.Predecessor is null
            && state.LineageCharges == CampaignStateFactory.EmptyChargesForAcceptance()
            && state.WorkItems.All(item =>
                item.OuterAttemptCount == 0
                && item.CandidateAttemptCount == 0
                && item.TrustedProposal is null
                && item.Status is CampaignWorkStatus.Planned or CampaignWorkStatus.Closed
                && (item.Status != CampaignWorkStatus.Closed
                    || item.ClosedOutcome?.Stage == CampaignWorkOutcomeStage.Planning))
            && state.TerminalOutcome == ExpectedInitialTerminal(state.WorkItems)
            && ArtifactsEqual(artifact, CampaignStateJson.CreateArtifact(state));
    }

    public static async ValueTask<CampaignCheckpointAcceptanceResult> AcceptInitialAsync(
        ICampaignCheckpointStore store,
        CampaignInitialCheckpointAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authority);
        return await AcceptCoreAsync(store, null, authority.Artifact, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<CampaignCheckpointAcceptanceResult> AcceptAsync(
        ICampaignCheckpointStore store,
        CampaignTransitionResult transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            return new CampaignCheckpointAcceptanceResult(
                CampaignCheckpointAcceptanceKind.InvalidTransition,
                null);
        }

        return await AcceptCoreAsync(
            store,
            transition.Predecessor,
            transition.Artifact,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CampaignCheckpointAcceptanceResult> AcceptCoreAsync(
        ICampaignCheckpointStore store,
        CampaignCheckpointArtifact? expectedPredecessor,
        CampaignCheckpointArtifact intended,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var currentMatch = Match(current, intended);
            if (currentMatch == MatchKind.Invalid)
            {
                return ReadFailure(current);
            }

            if (currentMatch == MatchKind.Exact)
            {
                return await VerifyReadbackAsync(
                    store,
                    intended,
                    grantsDispatch: false,
                    cancellationToken).ConfigureAwait(false);
            }

            CampaignCheckpointWriteResult write;
            if (expectedPredecessor is null)
            {
                if (current.Kind != CampaignCheckpointReadKind.NotFound)
                {
                    return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Conflict, null);
                }

                write = await store.CreateIfAbsentAsync(
                    intended.ExactUtf8Json.AsMemory(),
                    intended.CheckpointRevision,
                    intended.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (Match(current, expectedPredecessor) != MatchKind.Exact)
                {
                    return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Conflict, null);
                }

                write = await store.ReplaceIfCurrentAsync(
                    expectedPredecessor.CheckpointRevision,
                    expectedPredecessor.Sha256,
                    intended.ExactUtf8Json.AsMemory(),
                    intended.CheckpointRevision,
                    intended.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }

            if (write.Kind != CampaignCheckpointWriteKind.Written)
            {
                if (write.Kind is CampaignCheckpointWriteKind.AlreadyPresent
                    or CampaignCheckpointWriteKind.PredecessorMissing
                    or CampaignCheckpointWriteKind.CurrentMismatch)
                {
                    var concurrent = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (Match(concurrent, intended) == MatchKind.Exact)
                    {
                        return new CampaignCheckpointAcceptanceResult(
                            CampaignCheckpointAcceptanceKind.Accepted,
                            new CampaignAcceptedCheckpoint(
                                intended,
                                CampaignAcceptedCheckpointAuthorityKind.Observer));
                    }
                }

                return new CampaignCheckpointAcceptanceResult(
                    write.Kind is CampaignCheckpointWriteKind.AlreadyPresent
                        or CampaignCheckpointWriteKind.PredecessorMissing
                        or CampaignCheckpointWriteKind.CurrentMismatch
                        ? CampaignCheckpointAcceptanceKind.Conflict
                        : CampaignCheckpointAcceptanceKind.WriteRejected,
                    null);
            }

            return await VerifyReadbackAsync(
                store,
                intended,
                grantsDispatch: intended.State.ActiveReservation is not null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Cancelled, null);
        }
    }

    private static async ValueTask<CampaignCheckpointAcceptanceResult> VerifyReadbackAsync(
        ICampaignCheckpointStore store,
        CampaignCheckpointArtifact intended,
        bool grantsDispatch,
        CancellationToken cancellationToken)
    {
        var readback = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Match(readback, intended) == MatchKind.Exact
            ? new CampaignCheckpointAcceptanceResult(
                CampaignCheckpointAcceptanceKind.Accepted,
                new CampaignAcceptedCheckpoint(
                    intended,
                    grantsDispatch
                        ? CampaignAcceptedCheckpointAuthorityKind.Writer
                        : CampaignAcceptedCheckpointAuthorityKind.Observer))
            : new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.ReadbackMismatch, null);
    }

    private static CampaignCheckpointAcceptanceResult ReadFailure(CampaignCheckpointReadResult read) =>
        new(
            read.Kind == CampaignCheckpointReadKind.Unreadable
                ? CampaignCheckpointAcceptanceKind.Unreadable
                : CampaignCheckpointAcceptanceKind.InvalidRead,
            null);

    private static MatchKind Match(
        CampaignCheckpointReadResult read,
        CampaignCheckpointArtifact artifact)
    {
        if (read.Kind == CampaignCheckpointReadKind.NotFound)
        {
            return MatchKind.Different;
        }

        if (read.Kind != CampaignCheckpointReadKind.Found
            || read.CheckpointRevision is null
            || read.Sha256 is null
            || read.ExactUtf8Json.IsDefault)
        {
            return MatchKind.Invalid;
        }

        var bytes = read.ExactUtf8Json.AsSpan();
        var recomputed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var parsed = CampaignStateJson.Parse(read.ExactUtf8Json.AsMemory());
        if (!string.Equals(recomputed, read.Sha256, StringComparison.Ordinal)
            || !parsed.IsValid
            || parsed.Artifact is null
            || parsed.Artifact.CheckpointRevision != read.CheckpointRevision)
        {
            return MatchKind.Invalid;
        }

        return read.CheckpointRevision == artifact.CheckpointRevision
            && string.Equals(read.Sha256, artifact.Sha256, StringComparison.Ordinal)
            && bytes.SequenceEqual(artifact.ExactUtf8Json.AsSpan())
            && parsed.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan())
            ? MatchKind.Exact
            : MatchKind.Different;
    }

    private enum MatchKind
    {
        Exact,
        Different,
        Invalid,
    }

    private static bool ArtifactsEqual(CampaignCheckpointArtifact left, CampaignCheckpointArtifact right) =>
        left.CheckpointRevision == right.CheckpointRevision
        && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
        && left.ExactUtf8Json.AsSpan().SequenceEqual(right.ExactUtf8Json.AsSpan());

    private static CampaignTerminalOutcome? ExpectedInitialTerminal(
        ImmutableArray<CampaignWorkItemState> workItems) => workItems.IsEmpty
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork)
            : workItems.All(item => item.Status == CampaignWorkStatus.Closed)
                ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
                : null;
}
