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
    int RetryAfterMilliseconds = 0);

public sealed record DocumentationScribeHostAdmission(
    int MaximumMilliseconds, DocumentationScribeDeadlineOwner DeadlineOwner);

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
        DocumentationScribeHostOperation operation, int milliseconds, bool chargesTool, long startedAt, DocumentationScribeDeadlineOwner deadlineOwner)
    {
        this.scope = scope; Operation = operation; MaximumMilliseconds = milliseconds;
        this.chargesTool = chargesTool; this.startedAt = startedAt; DeadlineOwner = deadlineOwner;
    }
    public DocumentationScribeHostOperation Operation { get; }
    public int MaximumMilliseconds { get; }
    public DocumentationScribeDeadlineOwner DeadlineOwner { get; }
    public int ElapsedMilliseconds => scope.Allowance.ElapsedSince(startedAt);
    public int RemainingMilliseconds => Math.Max(0, MaximumMilliseconds - ElapsedMilliseconds);
    public bool TryBeginOperation() => !scope.Failed && RemainingMilliseconds > 0 && !scope.Allowance.HasCheckedStop
        && Interlocked.CompareExchange(ref started, 1, 0) == 0
        && (!chargesTool || scope.Allowance.TryChargeTool());
    internal bool TryComplete() => Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    public override string ToString() => nameof(DocumentationScribeHostPermit);
}

/// <summary>One validated Runtime scope over the already shared invocation allowance.</summary>
public sealed class DocumentationScribeExecutionScope
{
    private readonly IDocumentationScribeExecutionOperations? operations;
    private DocumentationScribeHostPermit? host;
    private DocumentationScribeInvocationProviderPermit? provider;
    private int failed;
    private int operationGrant;
    internal DocumentationScribeExecutionScope(DocumentationScribeInvocationAllowance allowance,
        IDocumentationScribeExecutionOperations? operations, int restoredFailures, int pendingRetryAfter)
    {
        Allowance = allowance; this.operations = operations;
        RestoredRetryableProviderFailures = restoredFailures; PendingRetryAfterMilliseconds = pendingRetryAfter;
    }
    public DocumentationScribeInvocationAllowance Allowance { get; }
    public int RestoredRetryableProviderFailures { get; }
    public int PendingRetryAfterMilliseconds { get; private set; }
    public int PhysicalProviderRequestCount { get; private set; }
    public bool LifetimeDeadlineReached { get; private set; }
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
        return new(allowance, operations, authority.RestoredRetryableProviderFailures, progress.PendingRetryAfterMilliseconds);
    }
    public async ValueTask<DocumentationScribeInvocationProviderPermit?> ReserveProviderAsync(
        DocumentationScribeDispatchDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (Failed || host is not null || provider is not null || !ValidDescriptor(descriptor)
            || Interlocked.CompareExchange(ref operationGrant, 1, 0) != 0) return null;
        if (!Allowance.TryReserveProvider(out var permit) || permit is null)
        { Volatile.Write(ref operationGrant, 0); return null; }
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
        if (!ReferenceEquals(provider, permit) || !Enum.IsDefined(settlement.Disposition)
            || !DocumentationScribeInvocationAllowance.ValidUsage(settlement.Usage)
            || settlement.RetryAfterMilliseconds is < 0 or > 300_000) return false;
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
            || Allowance.HasCheckedStop) return null;
        if (Allowance.RemainingMilliseconds <= 0) { Allowance.CanBeginProviderWork(); return null; }
        if (Interlocked.CompareExchange(ref operationGrant, 1, 0) != 0) return null;
        var startedAt = Allowance.Clock.GetTimestamp();
        var safety = operation switch
        {
            DocumentationScribeHostOperation.RepositoryTool => 30_000,
            DocumentationScribeHostOperation.SemanticTool => 10_000,
            DocumentationScribeHostOperation.RetryWait => PendingRetryAfterMilliseconds,
            _ => Allowance.Limits.MaximumRequestElapsedMilliseconds,
        };
        var remaining = Allowance.RemainingMilliseconds;
        var milliseconds = Math.Min(safety, remaining);
        var deadlineOwner = remaining <= safety ? DocumentationScribeDeadlineOwner.Invocation : DocumentationScribeDeadlineOwner.HostOperation;
        if (milliseconds <= 0) { Volatile.Write(ref operationGrant, 0); return null; }
        if (operations is not null)
        {
            var admitted = await operations.BeginHostAsync(operation, milliseconds, cancellationToken).ConfigureAwait(false);
            if (admitted is null) { Volatile.Write(ref operationGrant, 0); Interlocked.Exchange(ref failed, 1); return null; }
            milliseconds = admitted.MaximumMilliseconds;
            deadlineOwner = admitted.DeadlineOwner;
        }
        var chargesTool = operation is DocumentationScribeHostOperation.RepositoryTool
            or DocumentationScribeHostOperation.SemanticTool or DocumentationScribeHostOperation.RegisteredTool
            or DocumentationScribeHostOperation.TerminalSubmission;
        host = new(this, operation, milliseconds, chargesTool, startedAt, deadlineOwner); return host;
    }
    public async ValueTask<bool> CompleteHostAsync(DocumentationScribeHostPermit permit, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(host, permit) || !permit.TryComplete()) return false;
        if (operations is not null && !await operations.CompleteHostAsync(permit.Operation, permit.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false))
        { Interlocked.Exchange(ref failed, 1); return false; }
        if (permit.Operation == DocumentationScribeHostOperation.RetryWait)
            PendingRetryAfterMilliseconds = Math.Max(0, PendingRetryAfterMilliseconds - permit.ElapsedMilliseconds);
        host = null; Volatile.Write(ref operationGrant, 0); return true;
    }
    private static bool ValidDescriptor(DocumentationScribeDispatchDescriptor descriptor) => descriptor is not null
        && descriptor.RequestCommitmentSha256.Length == 64
        && descriptor.RequestCommitmentSha256.All(character => char.IsAsciiHexDigitLower(character) || character is >= '0' and <= '9')
        && descriptor.LogicalAttemptNumber is > 0 and <= DocumentationScribeContract.MaximumAttempts
        && descriptor.ProviderRequestNumber is > 0 and <= 128 && descriptor.MaximumOutputTokens > 0
        && descriptor.MaximumOutputTokens <= DocumentationScribeContract.MaximumConfiguredOutputTokens;
    public override string ToString() => nameof(DocumentationScribeExecutionScope);
}
