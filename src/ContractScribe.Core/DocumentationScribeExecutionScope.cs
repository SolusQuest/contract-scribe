namespace ContractScribe.Core;

public enum DocumentationScribeHostOperation
{
    Preflight, RepositoryTool, SemanticTool, RegisteredTool, TerminalSubmission,
    RetryWait, Continuation, Postflight, Retirement,
}

public enum DocumentationScribeDispatchDisposition
{
    Success, RetryableFailure, TerminalFailure, Interrupted,
}

public sealed record DocumentationScribeDispatchDescriptor(
    string RequestCommitmentSha256, int LogicalAttemptNumber, int ProviderRequestNumber,
    int MaximumOutputTokens);

public sealed record DocumentationScribeDispatchSettlement(
    DocumentationScribeDispatchDisposition Disposition, DocumentationScribeDispatchUsage? Usage,
    int RetryAfterMilliseconds = 0,
    DocumentationScribeCacheObservation Cache = DocumentationScribeCacheObservation.NotReported);

public sealed record DocumentationScribeHostAdmission(
    int MaximumMilliseconds, DocumentationScribeDeadlineOwner DeadlineOwner)
{
    internal CampaignSemanticCheckpointWindow? SemanticWindow { get; init; }
}

/// <summary>Closed scalar operations; persistence and accepted authority remain with the host.</summary>
public interface IDocumentationScribeExecutionOperations
{
    ValueTask<bool> ReserveProviderAsync(DocumentationScribeDispatchDescriptor descriptor,
        DocumentationScribeInvocationProviderPermit permit, CancellationToken cancellationToken);
    ValueTask<bool> SettleProviderAsync(DocumentationScribeInvocationProviderPermit permit,
        DocumentationScribeDispatchSettlement settlement, CancellationToken cancellationToken);
    ValueTask<DocumentationScribeHostAdmission?> BeginHostAsync(DocumentationScribeHostOperation operation,
        int maximumMilliseconds, CancellationToken cancellationToken);
    ValueTask<bool> CompleteHostAsync(DocumentationScribeHostOperation operation,
        int elapsedMilliseconds, CancellationToken cancellationToken);
}

public sealed class DocumentationScribeHostPermit
{
    private readonly DocumentationScribeExecutionScope scope;
    private readonly long startedAt;
    private readonly bool chargesTool;
    private int started;
    private int completed;
    internal DocumentationScribeHostPermit(DocumentationScribeExecutionScope scope,
        DocumentationScribeHostOperation operation, int milliseconds, bool chargesTool, long startedAt,
        DocumentationScribeDeadlineOwner deadlineOwner, CampaignSemanticCheckpointWindow? semanticWindow = null)
    {
        this.scope = scope; Operation = operation; MaximumMilliseconds = milliseconds;
        this.chargesTool = chargesTool; this.startedAt = startedAt; DeadlineOwner = deadlineOwner;
        SemanticWindow = semanticWindow;
    }
    public DocumentationScribeHostOperation Operation { get; }
    public int MaximumMilliseconds { get; }
    public DocumentationScribeDeadlineOwner DeadlineOwner { get; }
    public int ElapsedMilliseconds => scope.Allowance.ElapsedSince(startedAt);
    internal CampaignSemanticCheckpointWindow? SemanticWindow { get; }
    public bool RequiresReleaseValidation => SemanticWindow is not null;
    public CancellationToken ExecutionCancellation => SemanticWindow?.OwnerLost ?? default;
    public bool ResultReleaseAccepted { get; internal set; } = true;
    public bool ResultReleaseDeadlineReached { get; internal set; }
    public int RemainingMilliseconds => SemanticWindow?.RemainingQueryMilliseconds
        ?? Math.Max(0, MaximumMilliseconds - ElapsedMilliseconds);
    public bool TryBeginOperation() => !scope.Failed && scope.CanBeginHost(Operation)
        && RemainingMilliseconds > 0 && !scope.Allowance.HasCheckedStop
        && Interlocked.CompareExchange(ref started, 1, 0) == 0
        && (SemanticWindow?.TryBeginQuery() ?? true)
        && (!chargesTool || scope.Allowance.TryChargeTool());
    // Ephemeral observations of the original tokens, never a new cancellation
    // source, deadline or authority. A caller/owner collision stays conservative.
    private sealed record CancellationObservation(CancellationToken Deadline, CancellationToken Execution, CancellationToken Caller, CancellationToken Linked);
    private CancellationObservation? cancellation;
    /// <summary>Observes the original Runtime tokens once, without creating or renewing a permit.</summary>
    public void ObserveCancellation(CancellationToken deadline, CancellationToken execution, CancellationToken caller, CancellationToken linked)
    {
        if (Interlocked.CompareExchange(ref cancellation, new(deadline, execution, caller, linked), null) is not null)
            throw new InvalidOperationException("scribe.deadline.already-bound");
    }
    internal bool IsHostDeadlineCancellation(CancellationToken token) => DeadlineOwner == DocumentationScribeDeadlineOwner.HostOperation
        && Volatile.Read(ref cancellation) is { } observed && token == observed.Linked && observed.Deadline.IsCancellationRequested
        && !observed.Execution.IsCancellationRequested && !observed.Caller.IsCancellationRequested;
    internal bool TryComplete() => Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    public override string ToString() => nameof(DocumentationScribeHostPermit);
}

/// <summary>One validated Runtime scope over the already shared invocation allowance.</summary>
public sealed class DocumentationScribeExecutionScope
{
    private readonly IDocumentationScribeExecutionOperations? operations;
    private DocumentationScribeHostPermit? host;
    internal DocumentationScribeDeadlineOwner? CurrentHostDeadlineOwner => host?.DeadlineOwner;
    internal bool IsHostDeadlineCancellation(CancellationToken token) => host?.IsHostDeadlineCancellation(token) == true;
    private DocumentationScribeInvocationProviderPermit? provider;
    private int failed;
    private int operationGrant;
    private string? campaignRequestSha;
    private long? runtimeStartedAt;
    private int runtimeMaximumMilliseconds;
    private (Guid LogicalCallId, int AttemptNumber)? nextProviderAttempt;
    internal DocumentationScribeExecutionScope(DocumentationScribeInvocationAllowance allowance,
        IDocumentationScribeExecutionOperations? operations, int restoredFailures, int pendingRetryAfter)
    {
        Allowance = allowance; MaximumTargets = allowance.Limits.MaximumTargets; this.operations = operations;
        RestoredRetryableProviderFailures = restoredFailures; PendingRetryAfterMilliseconds = pendingRetryAfter;
    }
    public DocumentationScribeInvocationAllowance Allowance { get; }
    public int MaximumTargets { get; private init; }
    public int RestoredRetryableProviderFailures { get; }
    public int PendingRetryAfterMilliseconds { get; private set; }
    public int PhysicalProviderRequestCount { get; private set; }
    public DocumentationScribeAttemptId? AttemptId { get; private init; }
    public bool LifetimeDeadlineReached { get; private set; }
    public bool StandaloneDeadlineReached => !Allowance.IsCampaignScope
        && Allowance.StopReason == DocumentationScribeInvocationStopReason.Elapsed;
    public bool SettledLifetimeBudgetExceeded { get; private set; }
    public bool LifetimeAdmissionDenied { get; private set; }
    public bool CheckpointCapacityReached { get; private set; }
    internal void ObserveCheckpointCapacity() => CheckpointCapacityReached = true;
    public bool ResourceBudgetReached => CheckpointCapacityReached || LifetimeDeadlineReached || LifetimeAdmissionDenied || SettledLifetimeBudgetExceeded || SemanticHeadroomReached
        || Allowance.IsCampaignScope && Allowance.HasCheckedStop;
    public bool SemanticHeadroomReached { get; private set; }
    public bool SemanticInvocationHeadroomReached { get; private set; }
    public bool SemanticAuthorizationTimedOut { get; private set; }
    public bool SemanticAuthorizationClosed { get; private set; }
    internal int RemainingTargetMilliseconds => runtimeStartedAt is { } start
        ? Math.Max(0, runtimeMaximumMilliseconds - Allowance.ElapsedSince(start)) : 0;
    internal void ObserveSemanticAuthorizationTimeout() => SemanticAuthorizationTimedOut = true;
    internal void ObserveSemanticAuthorizationClosed() => SemanticAuthorizationClosed = true;
    internal void ObserveSemanticHeadroom()
    {
        SemanticHeadroomReached = true;
        SemanticInvocationHeadroomReached = Allowance.RemainingMilliseconds <= RemainingTargetMilliseconds;
    }

    /// <summary>Binds the original Runtime window once; tools cannot renew it.</summary>
    public void BindRuntimeWindow(DocumentationScribeRequest request, TimeProvider clock, long startedAt)
    {
        if (operations is null) return;
        if (runtimeStartedAt is not null || request.ArtifactSha256 != campaignRequestSha
            || !ReferenceEquals(clock, Allowance.Clock))
            throw new InvalidOperationException("scribe.scope.runtime-authority-mismatch");
        runtimeStartedAt = startedAt;
        runtimeMaximumMilliseconds = request.Limits.MaximumElapsedMilliseconds;
    }
    internal void ObserveSettledLifetimeBudgetExhaustion() => SettledLifetimeBudgetExceeded = true;
    internal void ObserveLifetimeAdmissionDenial() => LifetimeAdmissionDenied = true;
    internal bool CanBeginHost(DocumentationScribeHostOperation operation) => !SemanticAuthorizationClosed
        && !SemanticHeadroomReached && !LifetimeDeadlineReached && !LifetimeAdmissionDenied
        && (!SettledLifetimeBudgetExceeded || operation is DocumentationScribeHostOperation.TerminalSubmission
            or DocumentationScribeHostOperation.Postflight);
    public void ObserveDeadline(DocumentationScribeInvocationProviderPermit permit)
    {
        if (!ReferenceEquals(provider, permit)) throw new InvalidOperationException("scribe.deadline.invalid-owner");
        ObserveDeadline(permit.DeadlineOwner);
    }
    public void ObserveDeadline(DocumentationScribeHostPermit permit)
    {
        if (!ReferenceEquals(host, permit)) throw new InvalidOperationException("scribe.deadline.invalid-owner");
        ObserveDeadline(permit.DeadlineOwner);
    }
    internal void ObserveDeadline(DocumentationScribeDeadlineOwner owner)
    {
        if (owner == DocumentationScribeDeadlineOwner.Lifetime) LifetimeDeadlineReached = true;
        // The admitted boundary owns the stop. Integer timer durations can expire
        // before a second global-clock read rounds to the same millisecond.
        if (owner == DocumentationScribeDeadlineOwner.Invocation) Allowance.ObserveElapsedDeadline();
    }
    public bool Failed => Volatile.Read(ref failed) != 0;
    public static DocumentationScribeExecutionScope ForStandalone(DocumentationScribeRequest request,
        TimeProvider? clock = null) => new(DocumentationScribeInvocationAllowance.ForStandalone(request, clock), null, 0, 0);
    internal static DocumentationScribeExecutionScope ForCampaign(CampaignProviderInvocationAuthority authority,
        DocumentationScribeInvocationAllowance allowance, IDocumentationScribeExecutionOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!allowance.IsCampaignScope || !authority.BindInvocationAllowance(allowance))
            throw new InvalidOperationException("scribe.scope.invalid-authority");
        var progress = ((CampaignProviderReservation)authority.AcceptedCheckpoint.Artifact.State.ActiveReservation!).RetryProgress;
        return new(allowance, operations, authority.RestoredRetryableProviderFailures, progress.PendingRetryAfterMilliseconds)
        {
            MaximumTargets = authority.Request.ExecutableTargetIds.Length,
            campaignRequestSha = authority.Request.ArtifactSha256,
            AttemptId = authority.AttemptId
        };
    }
    public async ValueTask<DocumentationScribeInvocationProviderPermit?> ReserveProviderAsync(
        DocumentationScribeDispatchDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (Failed || SemanticAuthorizationClosed || ResourceBudgetReached || host is not null || provider is not null || !ValidDescriptor(descriptor)
            || Interlocked.CompareExchange(ref operationGrant, 1, 0) != 0) return null;
        if (!Allowance.TryReserveProvider(out var permit) || permit is null)
        { Volatile.Write(ref operationGrant, 0); return null; }
        permit.LogicalCallId = nextProviderAttempt?.LogicalCallId;
        permit.PhysicalAttemptNumber = nextProviderAttempt?.AttemptNumber;
        nextProviderAttempt = null;
        provider = permit;
        if (operations is not null && !await operations.ReserveProviderAsync(descriptor, permit, cancellationToken).ConfigureAwait(false))
        {
            Allowance.SettleProvider(permit, null); provider = null; Volatile.Write(ref operationGrant, 0); Interlocked.Exchange(ref failed, 1); return null;
        }
        return permit;
    }
    public bool BeginPhysicalDispatch(DocumentationScribeInvocationProviderPermit permit)
    {
        if (!ReferenceEquals(provider, permit) || !permit.TryBeginDispatch()) return false;
        PhysicalProviderRequestCount = checked(PhysicalProviderRequestCount + 1); return true;
    }
    public async ValueTask<bool> SettleProviderAsync(DocumentationScribeInvocationProviderPermit permit,
        DocumentationScribeDispatchSettlement settlement, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(provider, permit) || !Enum.IsDefined(settlement.Disposition) || !Enum.IsDefined(settlement.Cache)
            || !DocumentationScribeInvocationAllowance.ValidUsage(settlement.Usage)
            || settlement.RetryAfterMilliseconds is < 0 or > 300_000) return false;
        Allowance.ObserveProviderUsage(permit, settlement.Usage);
        if (operations is not null && !await operations.SettleProviderAsync(permit, settlement, cancellationToken).ConfigureAwait(false))
        { Interlocked.Exchange(ref failed, 1); return false; }
        if (!Allowance.SettleProvider(permit, settlement.Usage)) { Interlocked.Exchange(ref failed, 1); return false; }
        provider = null; Volatile.Write(ref operationGrant, 0);
        PendingRetryAfterMilliseconds = settlement.Disposition == DocumentationScribeDispatchDisposition.RetryableFailure
            ? settlement.RetryAfterMilliseconds : 0;
        return true;
    }
    public async ValueTask<DocumentationScribeHostPermit?> BeginHostAsync(DocumentationScribeHostOperation operation,
        CancellationToken cancellationToken)
    {
        if (Failed || host is not null || provider is not null || !Enum.IsDefined(operation)
            || Allowance.HasCheckedStop || !CanBeginHost(operation)) return null;
        if (Allowance.RemainingMilliseconds <= 0) { Allowance.CanBeginProviderWork(); return null; }
        if (Interlocked.CompareExchange(ref operationGrant, 1, 0) != 0) return null;
        var startedAt = Allowance.Clock.GetTimestamp();
        var semantic = operations is not null && operation == DocumentationScribeHostOperation.SemanticTool;
        var safety = operation switch
        {
            DocumentationScribeHostOperation.RepositoryTool => 30_000,
            DocumentationScribeHostOperation.SemanticTool => 10_000,
            DocumentationScribeHostOperation.RetryWait => PendingRetryAfterMilliseconds,
            _ => Allowance.Limits.MaximumRequestElapsedMilliseconds,
        };
        var remaining = Allowance.RemainingMilliseconds;
        var milliseconds = semantic
            ? Math.Min(CampaignSemanticCheckpointWindow.MaximumEnvelopeMilliseconds, Math.Min(remaining, RemainingTargetMilliseconds))
            : Math.Min(safety, remaining);
        if (semantic && milliseconds <= CampaignSemanticCheckpointWindow.MaximumClosureMilliseconds)
        {
            ObserveSemanticHeadroom();
            Volatile.Write(ref operationGrant, 0);
            return null;
        }
        var deadlineOwner = remaining <= safety ? DocumentationScribeDeadlineOwner.Invocation : DocumentationScribeDeadlineOwner.HostOperation;
        if (milliseconds <= 0) { Volatile.Write(ref operationGrant, 0); return null; }
        CampaignSemanticCheckpointWindow? semanticWindow = null;
        if (operations is not null)
        {
            var admitted = await operations.BeginHostAsync(operation, milliseconds, cancellationToken).ConfigureAwait(false);
            if (admitted is null)
            {
                Volatile.Write(ref operationGrant, 0);
                if (!SemanticAuthorizationClosed) Interlocked.Exchange(ref failed, 1);
                return null;
            }
            milliseconds = admitted.MaximumMilliseconds;
            deadlineOwner = admitted.DeadlineOwner;
            semanticWindow = admitted.SemanticWindow;
            if (semantic && semanticWindow is null)
            { Volatile.Write(ref operationGrant, 0); Interlocked.Exchange(ref failed, 1); return null; }
        }
        var chargesTool = operation is DocumentationScribeHostOperation.RepositoryTool
            or DocumentationScribeHostOperation.SemanticTool or DocumentationScribeHostOperation.RegisteredTool
            or DocumentationScribeHostOperation.TerminalSubmission;
        host = new(this, operation, milliseconds, chargesTool, startedAt, deadlineOwner, semanticWindow); return host;
    }
    public async ValueTask<bool> CompleteHostAsync(DocumentationScribeHostPermit permit, CancellationToken cancellationToken,
        IDocumentationScribeToolResultReleaseValidator? resultReleaseValidator = null,
        CancellationToken resultCancellation = default)
    {
        if (!ReferenceEquals(host, permit) || !permit.TryComplete()) return false;
        permit.SemanticWindow?.BeginClosing();
        try
        {
            if (operations is not null && !await operations.CompleteHostAsync(permit.Operation, permit.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false))
            { Interlocked.Exchange(ref failed, 1); return false; }
            if (permit.SemanticWindow is { } window && resultReleaseValidator is not null)
            {
                permit.ResultReleaseAccepted = false;
                try
                {
                    using var validation = CancellationTokenSource.CreateLinkedTokenSource(
                        window.ObservationToken, window.OwnerLost, resultCancellation);
                    permit.ResultReleaseAccepted = window.ObservationOpen && window.OwnerCurrent
                        && !validation.IsCancellationRequested
                        && resultReleaseValidator.ValidateResultRelease(validation.Token)
                        && !validation.IsCancellationRequested
                        && window.ObservationOpen && window.OwnerCurrent;
                }
                catch (OperationCanceledException) { }
                permit.ResultReleaseDeadlineReached = !window.ObservationOpen;
            }
            if (permit.Operation == DocumentationScribeHostOperation.RetryWait)
                PendingRetryAfterMilliseconds = Math.Max(0, PendingRetryAfterMilliseconds - permit.ElapsedMilliseconds);
            host = null; Volatile.Write(ref operationGrant, 0); return true;
        }
        finally { permit.SemanticWindow?.Dispose(); }
    }
    private static bool ValidDescriptor(DocumentationScribeDispatchDescriptor descriptor) => descriptor is not null
        && descriptor.RequestCommitmentSha256.Length == 64
        && descriptor.RequestCommitmentSha256.All(character => char.IsAsciiHexDigitLower(character) || character is >= '0' and <= '9')
        && descriptor.LogicalAttemptNumber is > 0 and <= DocumentationScribeContract.MaximumAttempts
        && descriptor.ProviderRequestNumber is > 0 and <= 128 && descriptor.MaximumOutputTokens > 0
        && descriptor.MaximumOutputTokens <= DocumentationScribeContract.MaximumConfiguredOutputTokens;
    public override string ToString() => nameof(DocumentationScribeExecutionScope);

    /// <summary>Optional SDK correlation only; cannot admit or start a provider operation.</summary>
    public void ObserveProviderAttempt(Guid logicalCallId, int attemptNumber) =>
        nextProviderAttempt = logicalCallId != Guid.Empty && attemptNumber is > 0 and <= 128
            ? (logicalCallId, attemptNumber) : null;
}
