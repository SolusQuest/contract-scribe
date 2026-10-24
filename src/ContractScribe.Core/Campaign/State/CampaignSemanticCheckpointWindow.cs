namespace ContractScribe.Core;

public enum CampaignSemanticCheckpointStoreKind { Local = 1, RemoteLeased = 2 }

/// <summary>The operation-local adapter seam for a campaign semantic checkpoint.</summary>
public interface ICampaignSemanticCheckpointStore : ICampaignCheckpointStore
{
    object SemanticCheckpointIdentity { get; }
    CampaignSemanticCheckpointStoreKind SemanticCheckpointKind { get; }
    IDisposable EnterSemanticCheckpointWindow(CampaignSemanticCheckpointWindow window);
}

/// <summary>
/// One non-restorable semantic authorization, query and observation window.
/// Its scalar envelope is already present in the exact checkpoint being accepted.
/// </summary>
public sealed class CampaignSemanticCheckpointWindow : IDisposable
{
    public const int MaximumAuthorizationMilliseconds = 30_000;
    public const int MaximumQueryMilliseconds = 10_000;
    public const int MaximumClosureMilliseconds = 30_000;
    public const int MaximumEnvelopeMilliseconds = 70_000;
    private readonly object storeIdentity;
    private readonly CampaignSemanticCheckpointStoreKind storeKind;
    private readonly string checkpointSha;
    private readonly long checkpointRevision;
    private readonly long startedAt;
    private readonly Func<bool>? progressGuard;
    private readonly CancellationTokenSource authorization;
    private readonly CancellationTokenSource authorizationObservation;
    private readonly CancellationToken authorizationToken;
    private readonly CancellationToken authorizationObservationToken;
    private CancellationTokenSource? closure;
    private CancellationToken closureToken;
    private int observationEnd;
    private int stage;
    private int queryStarted;
    private int disposed;
    private bool observed;
    private bool abandoned;
    private long leaseObservedAt;
    private int? leaseMilliseconds;
    private CancellationToken ownerLost;
    private Func<bool>? ownerCurrent;
    private long queryAcceptedAt;
    private int queryMilliseconds;
    private IDisposable? binding;

    internal CampaignSemanticCheckpointWindow(ICampaignSemanticCheckpointStore store,
        CampaignCheckpointArtifact intended, TimeProvider clock, long startedAt, int envelopeMilliseconds,
        Func<bool>? progressGuard = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(intended);
        ArgumentNullException.ThrowIfNull(clock);
        if (!Enum.IsDefined(store.SemanticCheckpointKind)
            || envelopeMilliseconds is <= MaximumClosureMilliseconds or > MaximumEnvelopeMilliseconds
            || intended.State.ActiveReservation is not CampaignProviderReservation
            { OperationKind: CampaignProviderOperationKind.Host, HostPhase: CampaignProviderHostPhase.SemanticTool } claim
            || claim.Exposure.ElapsedMilliseconds != envelopeMilliseconds)
            throw new ArgumentException("Invalid semantic checkpoint window.", nameof(intended));
        storeIdentity = store.SemanticCheckpointIdentity ?? throw new ArgumentException("Missing semantic store identity.", nameof(store));
        storeKind = store.SemanticCheckpointKind;
        checkpointSha = intended.Sha256;
        checkpointRevision = intended.CheckpointRevision;
        Clock = clock;
        this.startedAt = startedAt;
        this.progressGuard = progressGuard;
        EnvelopeMilliseconds = envelopeMilliseconds;
        AuthorizationMilliseconds = Math.Min(MaximumAuthorizationMilliseconds,
            envelopeMilliseconds - MaximumClosureMilliseconds);
        observationEnd = Math.Min(envelopeMilliseconds, AuthorizationMilliseconds + MaximumClosureMilliseconds);
        authorization = new(TimeSpan.FromMilliseconds(Math.Max(0, AuthorizationMilliseconds - ElapsedMilliseconds)), clock);
        authorizationObservation = new(TimeSpan.FromMilliseconds(Math.Max(0, observationEnd - ElapsedMilliseconds)), clock);
        authorizationToken = authorization.Token;
        authorizationObservationToken = authorizationObservation.Token;
    }

    public TimeProvider Clock { get; }
    public int EnvelopeMilliseconds { get; }
    public int AuthorizationMilliseconds { get; }
    public int ElapsedMilliseconds => Elapsed(startedAt);
    public CancellationToken AuthorizationToken => authorizationToken;
    public CancellationToken ObservationToken => Volatile.Read(ref stage) == 2 ? closureToken : authorizationObservationToken;
    public int RemainingObservationMilliseconds => Math.Max(0, observationEnd - ElapsedMilliseconds);
    public bool IsClosingObservation => Volatile.Read(ref stage) == 2;
    public bool ObservationOpen => Volatile.Read(ref disposed) == 0
        && RemainingObservationMilliseconds > 0 && !ObservationToken.IsCancellationRequested;
    internal bool AuthorizationOpen => Volatile.Read(ref disposed) == 0 && !abandoned
        && Volatile.Read(ref stage) == 0 && ElapsedMilliseconds < AuthorizationMilliseconds
        && !authorizationToken.IsCancellationRequested;
    internal CancellationToken OwnerLost => ownerLost;
    internal bool OwnerCurrent => !ownerLost.IsCancellationRequested
        && (ownerCurrent?.Invoke() ?? storeKind == CampaignSemanticCheckpointStoreKind.Local)
        && (progressGuard?.Invoke() ?? true);
    internal int RemainingQueryMilliseconds
    {
        get
        {
            if (Volatile.Read(ref stage) != 1 || abandoned) return 0;
            var now = Clock.GetTimestamp();
            var leaseRemaining = leaseMilliseconds is { } lease ? lease - Elapsed(leaseObservedAt, now) : int.MaxValue;
            return Math.Max(0, Math.Min(queryMilliseconds - Elapsed(queryAcceptedAt, now),
                Math.Min(EnvelopeMilliseconds - MaximumClosureMilliseconds - Elapsed(startedAt, now), leaseRemaining)));
        }
    }

    internal void AttachBinding(IDisposable value) => binding = value;

    public void ObserveLocalReadback(object identity, CampaignCheckpointReadResult readback)
    {
        if (storeKind == CampaignSemanticCheckpointStoreKind.Local && Matches(identity, readback))
            observed = true;
    }

    public void ObserveRemoteReadback(object identity, CampaignCheckpointReadResult readback,
        int remainingLeaseMilliseconds, CancellationToken permanentOwnerLoss, Func<bool> isCurrentOwner)
    {
        ArgumentNullException.ThrowIfNull(isCurrentOwner);
        if (storeKind != CampaignSemanticCheckpointStoreKind.RemoteLeased || remainingLeaseMilliseconds <= 0
            || !permanentOwnerLoss.CanBeCanceled || permanentOwnerLoss.IsCancellationRequested
            || !Matches(identity, readback) || !isCurrentOwner()) return;
        leaseObservedAt = Clock.GetTimestamp();
        leaseMilliseconds = remainingLeaseMilliseconds;
        ownerLost = permanentOwnerLoss;
        ownerCurrent = isCurrentOwner;
        observed = true;
    }

    private bool Matches(object identity, CampaignCheckpointReadResult readback) => AuthorizationOpen
        && ReferenceEquals(identity, storeIdentity) && readback.Kind == CampaignCheckpointReadKind.Found
        && readback.CheckpointRevision == checkpointRevision && readback.Sha256 == checkpointSha;

    internal bool TryIssue(CampaignCheckpointArtifact accepted, long acceptedAt, int invocationRemaining, int targetRemaining,
        out int milliseconds)
    {
        milliseconds = 0;
        if (!AuthorizationOpen || !observed || accepted.CheckpointRevision != checkpointRevision
            || accepted.Sha256 != checkpointSha || !OwnerCurrent
            || storeKind == CampaignSemanticCheckpointStoreKind.RemoteLeased && leaseMilliseconds is null) return false;
        var now = Clock.GetTimestamp();
        var acceptedElapsed = Elapsed(acceptedAt, now);
        var leaseRemaining = leaseMilliseconds is { } lease ? Math.Max(0, lease - Elapsed(leaseObservedAt, now)) : int.MaxValue;
        milliseconds = Math.Max(0, Math.Min(MaximumQueryMilliseconds - acceptedElapsed, Math.Min(leaseRemaining,
            Math.Min(EnvelopeMilliseconds - MaximumClosureMilliseconds - Elapsed(startedAt, now),
                Math.Min(invocationRemaining, targetRemaining)))));
        if (milliseconds <= 0 || Interlocked.CompareExchange(ref stage, 1, 0) != 0) return false;
        // Both the original ten-second bound and any shorter authority remain
        // anchored before post-acceptance callbacks, never at permit issuance.
        queryAcceptedAt = acceptedAt;
        queryMilliseconds = checked(acceptedElapsed + milliseconds);
        return true;
    }

    internal bool TryBeginQuery() => Volatile.Read(ref disposed) == 0 && OwnerCurrent
        && RemainingQueryMilliseconds > 0 && Interlocked.CompareExchange(ref queryStarted, 1, 0) == 0;

    internal void AbandonQuery() => abandoned = true;

    internal void BeginClosing()
    {
        var previous = Interlocked.Exchange(ref stage, 2);
        if (previous == 2 || Volatile.Read(ref disposed) != 0) return;
        var boundary = previous == 0 ? Math.Min(ElapsedMilliseconds, AuthorizationMilliseconds) : ElapsedMilliseconds;
        observationEnd = Math.Min(EnvelopeMilliseconds, checked(boundary + MaximumClosureMilliseconds));
        closure = new(TimeSpan.FromMilliseconds(RemainingObservationMilliseconds), Clock);
        closureToken = closure.Token;
        if (RemainingObservationMilliseconds == 0) closure.Cancel();
    }

    private int Elapsed(long from) => Elapsed(from, Clock.GetTimestamp());
    private int Elapsed(long from, long to) => (int)Math.Clamp(Math.Ceiling(Clock.GetElapsedTime(from, to).TotalMilliseconds), 0, int.MaxValue);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        abandoned = true;
        authorization.Cancel();
        authorizationObservation.Cancel();
        closure?.Cancel();
        binding?.Dispose();
        closure?.Dispose();
        authorizationObservation.Dispose();
        authorization.Dispose();
    }

    public override string ToString() => nameof(CampaignSemanticCheckpointWindow);
}
