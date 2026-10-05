namespace ContractScribe.Core;

/// <summary>One invocation's operational policy; it is never a correctness identity.</summary>
public sealed record DocumentationScribeInvocationLimits
{
    private DocumentationScribeInvocationLimits(
        int maximumTargets, int maximumProviderRequests, int maximumToolCalls,
        int maximumCachedInputTokens, int maximumUncachedInputTokens, int maximumOutputTokens,
        int maximumElapsedMilliseconds, int maximumRequestOutputTokens,
        int maximumRequestElapsedMilliseconds, int maximumConnectMilliseconds,
        int maximumToolCallsPerResponse)
    {
        MaximumTargets = maximumTargets;
        MaximumProviderRequests = maximumProviderRequests;
        MaximumToolCalls = maximumToolCalls;
        MaximumCachedInputTokens = maximumCachedInputTokens;
        MaximumUncachedInputTokens = maximumUncachedInputTokens;
        MaximumOutputTokens = maximumOutputTokens;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
        MaximumRequestOutputTokens = maximumRequestOutputTokens;
        MaximumRequestElapsedMilliseconds = maximumRequestElapsedMilliseconds;
        MaximumConnectMilliseconds = maximumConnectMilliseconds;
        MaximumToolCallsPerResponse = maximumToolCallsPerResponse;
    }

    public int MaximumTargets { get; }
    public int MaximumProviderRequests { get; }
    public int MaximumToolCalls { get; }
    public int MaximumCachedInputTokens { get; }
    public int MaximumUncachedInputTokens { get; }
    public int MaximumOutputTokens { get; }
    public int MaximumElapsedMilliseconds { get; }
    public int MaximumRequestOutputTokens { get; }
    public int MaximumRequestElapsedMilliseconds { get; }
    public int MaximumConnectMilliseconds { get; }
    public int MaximumToolCallsPerResponse { get; }

    public static DocumentationScribeInvocationLimits Create(
        int maximumTargets = 100, int maximumProviderRequests = 64, int maximumToolCalls = 512,
        int maximumCachedInputTokens = 38_000_000, int maximumUncachedInputTokens = 2_000_000,
        int maximumOutputTokens = 524_288, int maximumElapsedMilliseconds = 900_000,
        int maximumRequestOutputTokens = 65_536, int maximumRequestElapsedMilliseconds = 300_000,
        int maximumConnectMilliseconds = 15_000, int maximumToolCallsPerResponse = 16)
    {
        Bound(maximumTargets, CampaignStateContract.MaximumWorkItems, nameof(maximumTargets));
        Bound(maximumProviderRequests, 128, nameof(maximumProviderRequests));
        Bound(maximumToolCalls, 1_024, nameof(maximumToolCalls));
        Bound(maximumCachedInputTokens, DocumentationScribeContract.MaximumConfiguredInputTokens, nameof(maximumCachedInputTokens));
        Bound(maximumUncachedInputTokens, DocumentationScribeContract.MaximumConfiguredInputTokens, nameof(maximumUncachedInputTokens));
        if (checked((long)maximumCachedInputTokens + maximumUncachedInputTokens)
            > DocumentationScribeContract.MaximumConfiguredInputTokens)
            throw new ArgumentOutOfRangeException(nameof(maximumCachedInputTokens));
        Bound(maximumOutputTokens, DocumentationScribeContract.MaximumConfiguredOutputTokens, nameof(maximumOutputTokens));
        Bound(maximumElapsedMilliseconds, DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds, nameof(maximumElapsedMilliseconds));
        Bound(maximumRequestOutputTokens, DocumentationScribeContract.MaximumConfiguredOutputTokens, nameof(maximumRequestOutputTokens));
        Bound(maximumRequestElapsedMilliseconds, DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds, nameof(maximumRequestElapsedMilliseconds));
        Bound(maximumConnectMilliseconds, DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds, nameof(maximumConnectMilliseconds));
        Bound(maximumToolCallsPerResponse, 16, nameof(maximumToolCallsPerResponse));
        return new(maximumTargets, maximumProviderRequests, maximumToolCalls,
            maximumCachedInputTokens, maximumUncachedInputTokens, maximumOutputTokens,
            maximumElapsedMilliseconds, maximumRequestOutputTokens, maximumRequestElapsedMilliseconds,
            maximumConnectMilliseconds, maximumToolCallsPerResponse);
    }

    internal static DocumentationScribeInvocationLimits ForStandalone(DocumentationScribeRunLimits limits) =>
        new(1, limits.MaximumProviderRequests, limits.MaximumToolCalls,
            limits.MaximumInputTokens, limits.MaximumUncachedInputTokens, limits.MaximumOutputTokens,
            limits.MaximumElapsedMilliseconds, limits.MaximumOutputTokens, limits.MaximumElapsedMilliseconds,
            Math.Min(15_000, limits.MaximumElapsedMilliseconds), 1_024);

    private static void Bound(int value, int maximum, string name)
    {
        if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}

public enum DocumentationScribeInvocationStopReason
{
    None, ProviderRequests, ToolCalls, CachedInputTokens, UncachedInputTokens,
    OutputTokens, Elapsed, RequestOutputTokens, RequestElapsed, ConnectElapsed, ResponseWidth,
    StandaloneInputTokens, StandaloneCost, InvalidAccounting,
}

public enum DocumentationScribeDeadlineOwner
{
    Provider, Invocation, Lifetime, HostOperation,
}

/// <summary>Bounded facts from one physical exchange, with absent fields remaining absent.</summary>
public sealed record DocumentationScribeDispatchUsage(
    int? InputTokens = null, int? CachedInputTokens = null, int? UncachedInputTokens = null,
    int? OutputTokens = null, int? ReasoningTokens = null,
    string? CurrencyId = null, long? CostMicrounits = null);

public sealed record DocumentationScribeDispatchExposure(
    int InputTokens, int CachedInputTokens, int UncachedInputTokens,
    int OutputTokens, int ElapsedMilliseconds);

/// <summary>A serial one-shot physical-send permit owned by its invocation allowance.</summary>
public sealed class DocumentationScribeInvocationProviderPermit
{
    private readonly DocumentationScribeInvocationAllowance owner;
    private int started;
    private int settled;

    internal DocumentationScribeInvocationProviderPermit(
        DocumentationScribeInvocationAllowance owner, DocumentationScribeDispatchExposure exposure,
        DocumentationScribeDeadlineOwner deadlineOwner, long startedAt)
    {
        this.owner = owner;
        Exposure = exposure;
        DeadlineOwner = deadlineOwner;
        StartedAt = startedAt;
    }

    public DocumentationScribeDispatchExposure Exposure { get; internal set; }
    public DocumentationScribeDeadlineOwner DeadlineOwner { get; internal set; }
    public bool DispatchStarted => Volatile.Read(ref started) == 1;
    public int RemainingMilliseconds => Math.Max(0, Exposure.ElapsedMilliseconds - owner.ElapsedSince(StartedAt));
    internal long StartedAt { get; }
    internal bool OwnedBy(DocumentationScribeInvocationAllowance allowance) => ReferenceEquals(owner, allowance);
    internal bool TrySettle() => Interlocked.CompareExchange(ref settled, 1, 0) == 0;
    internal bool TryStart() => Interlocked.CompareExchange(ref started, 1, 0) == 0;

    public bool TryBeginDispatch() => owner.TryBeginDispatch(this);
    public override string ToString() => nameof(DocumentationScribeInvocationProviderPermit);
}

/// <summary>One process-local counter/clock owner, shared by every target and retry.</summary>
public sealed class DocumentationScribeInvocationAllowance
{
    private readonly object gate = new();
    private readonly long startedAt;
    private readonly int? standaloneInputLimit;
    private readonly long? standaloneCostLimit;
    private DocumentationScribeInvocationProviderPermit? active;
    private CampaignChargeObservation input = new(null, 0, 0);
    private CampaignChargeObservation cached = new(null, 0, 0);
    private CampaignChargeObservation uncached = new(null, 0, 0);
    private CampaignChargeObservation output = new(null, 0, 0);
    private long observedCost;
    private bool hasObservedCost;
    private int requests;
    private int tools;
    private DocumentationScribeInvocationStopReason stop;

    public DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits limits, TimeProvider? clock = null)
        : this(limits, clock ?? TimeProvider.System, null, null) { }

    private DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits limits,
        TimeProvider clock, int? standaloneInputLimit, long? standaloneCostLimit)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        Limits = limits;
        Clock = clock;
        this.standaloneInputLimit = standaloneInputLimit;
        this.standaloneCostLimit = standaloneCostLimit;
        startedAt = clock.GetTimestamp();
    }

    public DocumentationScribeInvocationLimits Limits { get; }
    public TimeProvider Clock { get; }
    public int ElapsedMilliseconds => ElapsedSince(startedAt);
    public int RemainingMilliseconds => Math.Max(0, Limits.MaximumElapsedMilliseconds - ElapsedMilliseconds);
    public DocumentationScribeInvocationStopReason StopReason { get { lock (gate) return stop; } }
    public int ProviderRequestCount { get { lock (gate) return requests; } }
    public int ToolCallCount { get { lock (gate) return tools; } }
    public int RemainingToolCalls { get { lock (gate) return Math.Max(0, Limits.MaximumToolCalls - tools); } }
    public int RemainingOutputTokens { get { lock (gate) return (int)Math.Max(0, Limits.MaximumOutputTokens - output.TotalCharged); } }
    public bool HasCheckedStop => StopReason != DocumentationScribeInvocationStopReason.None;

    public static DocumentationScribeInvocationAllowance ForStandalone(
        DocumentationScribeRequest request, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(DocumentationScribeInvocationLimits.ForStandalone(request.Limits), clock ?? TimeProvider.System,
            request.Limits.MaximumInputTokens, request.Limits.MaximumCostMicrounits);
    }

    internal bool IsCampaignScope => standaloneInputLimit is null;

    public bool CanBeginProviderWork()
    {
        lock (gate) return CheckProviderCapacity();
    }

    internal DocumentationScribeDispatchExposure PreviewProviderExposure()
    {
        lock (gate)
        {
            if (!CheckProviderCapacity()) throw new InvalidOperationException("scribe.invocation.exhausted");
            var hit = checked((int)(Limits.MaximumCachedInputTokens - cached.TotalCharged));
            var miss = checked((int)(Limits.MaximumUncachedInputTokens - uncached.TotalCharged));
            var total = standaloneInputLimit is { } limit
                ? checked((int)(limit - input.TotalCharged)) : checked(hit + miss);
            return new(total, hit, miss, Math.Min(Limits.MaximumRequestOutputTokens, RemainingOutputTokens),
                Math.Min(Limits.MaximumRequestElapsedMilliseconds, RemainingMilliseconds));
        }
    }

    public bool TryReserveProvider(out DocumentationScribeInvocationProviderPermit? permit)
    {
        lock (gate)
        {
            permit = null;
            if (active is not null || !CheckProviderCapacity()) return false;
            var exposure = PreviewProviderExposure();
            permit = new(this, exposure,
                RemainingMilliseconds <= Limits.MaximumRequestElapsedMilliseconds
                    ? DocumentationScribeDeadlineOwner.Invocation : DocumentationScribeDeadlineOwner.Provider,
                Clock.GetTimestamp());
            active = permit;
            return true;
        }
    }

    public bool TryChargeTool()
    {
        lock (gate)
        {
            if (active is not null || HasCheckedStop) return false;
            if (RemainingMilliseconds <= 0) return Stop(DocumentationScribeInvocationStopReason.Elapsed);
            if (tools >= Limits.MaximumToolCalls) return Stop(DocumentationScribeInvocationStopReason.ToolCalls);
            tools = checked(tools + 1);
            return true;
        }
    }

    public bool SettleProvider(DocumentationScribeInvocationProviderPermit permit,
        DocumentationScribeDispatchUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(permit);
        lock (gate)
        {
            if (!ReferenceEquals(active, permit) || !permit.OwnedBy(this) || !ValidUsage(usage)) return false;
            try
            {
                var sent = permit.DispatchStarted;
                var exposure = permit.Exposure;
                var nextInput = sent ? CampaignBudgetAccounting.ChargeDispatchDimension(input,
                    usage?.InputTokens ?? CompleteInput(usage), exposure.InputTokens,
                    checked((long)(usage?.CachedInputTokens ?? 0) + (usage?.UncachedInputTokens ?? 0))) : input;
                var nextCached = sent ? CampaignBudgetAccounting.ChargeDispatchDimension(cached,
                    usage?.CachedInputTokens, exposure.CachedInputTokens) : cached;
                var nextUncached = sent ? CampaignBudgetAccounting.ChargeDispatchDimension(uncached,
                    usage?.UncachedInputTokens, exposure.UncachedInputTokens) : uncached;
                var nextOutput = sent ? CampaignBudgetAccounting.ChargeDispatchDimension(output,
                    usage?.OutputTokens, exposure.OutputTokens, usage?.ReasoningTokens ?? 0) : output;
                var nextCost = sent ? checked(observedCost + (usage?.CostMicrounits ?? 0)) : observedCost;
                if (!permit.TrySettle()) return false;
                input = nextInput; cached = nextCached; uncached = nextUncached; output = nextOutput;
                observedCost = nextCost;
                hasObservedCost |= sent && usage?.CostMicrounits is not null;
                active = null;
                if (cached.TotalCharged > Limits.MaximumCachedInputTokens) Stop(DocumentationScribeInvocationStopReason.CachedInputTokens);
                if (uncached.TotalCharged > Limits.MaximumUncachedInputTokens) Stop(DocumentationScribeInvocationStopReason.UncachedInputTokens);
                if (output.TotalCharged > Limits.MaximumOutputTokens) Stop(DocumentationScribeInvocationStopReason.OutputTokens);
                if (standaloneInputLimit is { } inputLimit && input.TotalCharged > inputLimit) Stop(DocumentationScribeInvocationStopReason.StandaloneInputTokens);
                if (standaloneCostLimit is { } costLimit && observedCost > costLimit) Stop(DocumentationScribeInvocationStopReason.StandaloneCost);
                if (RemainingMilliseconds <= 0) Stop(DocumentationScribeInvocationStopReason.Elapsed);
                return true;
            }
            catch (OverflowException)
            {
                return Stop(DocumentationScribeInvocationStopReason.InvalidAccounting);
            }
        }
    }

    internal bool TryBeginDispatch(DocumentationScribeInvocationProviderPermit permit)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, permit) || !permit.OwnedBy(this)
                || permit.RemainingMilliseconds <= 0 || RemainingMilliseconds <= 0
                || requests >= Limits.MaximumProviderRequests || !permit.TryStart()) return false;
            requests = checked(requests + 1);
            return true;
        }
    }

    internal void ShortenForLifetime(DocumentationScribeInvocationProviderPermit permit, int milliseconds)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, permit) || permit.DispatchStarted
                || milliseconds <= 0 || milliseconds > permit.Exposure.ElapsedMilliseconds)
                throw new InvalidOperationException("scribe.invocation.invalid-deadline");
            permit.Exposure = permit.Exposure with { ElapsedMilliseconds = milliseconds };
            permit.DeadlineOwner = DocumentationScribeDeadlineOwner.Lifetime;
        }
    }

    internal int ElapsedSince(long timestamp)
    {
        var elapsed = Clock.GetElapsedTime(timestamp).TotalMilliseconds;
        if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > int.MaxValue)
            throw new InvalidOperationException("scribe.invocation.invalid-clock");
        return checked((int)Math.Ceiling(elapsed));
    }

    internal static bool ValidUsage(DocumentationScribeDispatchUsage? usage)
    {
        if (usage is null) return true;
        var inputs = new[] { usage.InputTokens, usage.CachedInputTokens, usage.UncachedInputTokens };
        if (inputs.Any(value => value is < 0 || value > DocumentationScribeContract.MaximumObservedInputTokens)
            || usage.OutputTokens is < 0 || usage.OutputTokens > DocumentationScribeContract.MaximumObservedOutputTokens
            || usage.ReasoningTokens is < 0 || usage.ReasoningTokens > DocumentationScribeContract.MaximumObservedOutputTokens
            || usage.OutputTokens is { } outputValue && usage.ReasoningTokens > outputValue
            || usage.CostMicrounits is < 0 || usage.CostMicrounits > DocumentationScribeContract.MaximumObservedCostMicrounits
            || (usage.CostMicrounits is null) != (usage.CurrencyId is null)
            || usage.CurrencyId is { } currency && (currency.Length is 0 or > 128
                || currency.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))) return false;
        if (usage.InputTokens is { } parent
            && (usage.CachedInputTokens > parent || usage.UncachedInputTokens > parent)) return false;
        return usage.InputTokens is not { } total || CompleteInput(usage) is not { } sum || total == sum;
    }

    internal static int? CompleteInput(DocumentationScribeDispatchUsage? usage) =>
        usage?.CachedInputTokens is { } hit && usage.UncachedInputTokens is { } miss
            ? checked(hit + miss) : null;

    private bool CheckProviderCapacity()
    {
        if (stop != DocumentationScribeInvocationStopReason.None) return false;
        if (RemainingMilliseconds <= 0) return Stop(DocumentationScribeInvocationStopReason.Elapsed);
        if (requests >= Limits.MaximumProviderRequests) return Stop(DocumentationScribeInvocationStopReason.ProviderRequests);
        if (tools >= Limits.MaximumToolCalls) return Stop(DocumentationScribeInvocationStopReason.ToolCalls);
        if (cached.TotalCharged >= Limits.MaximumCachedInputTokens) return Stop(DocumentationScribeInvocationStopReason.CachedInputTokens);
        if (uncached.TotalCharged >= Limits.MaximumUncachedInputTokens) return Stop(DocumentationScribeInvocationStopReason.UncachedInputTokens);
        if (output.TotalCharged >= Limits.MaximumOutputTokens) return Stop(DocumentationScribeInvocationStopReason.OutputTokens);
        if (Limits.MaximumRequestOutputTokens == 0) return Stop(DocumentationScribeInvocationStopReason.RequestOutputTokens);
        if (Limits.MaximumRequestElapsedMilliseconds == 0) return Stop(DocumentationScribeInvocationStopReason.RequestElapsed);
        if (Limits.MaximumConnectMilliseconds == 0) return Stop(DocumentationScribeInvocationStopReason.ConnectElapsed);
        if (Limits.MaximumToolCallsPerResponse == 0) return Stop(DocumentationScribeInvocationStopReason.ResponseWidth);
        if (standaloneInputLimit is { } inputLimit && input.TotalCharged >= inputLimit) return Stop(DocumentationScribeInvocationStopReason.StandaloneInputTokens);
        if (standaloneCostLimit is { } costLimit && hasObservedCost && observedCost >= costLimit) return Stop(DocumentationScribeInvocationStopReason.StandaloneCost);
        return true;
    }

    private bool Stop(DocumentationScribeInvocationStopReason reason)
    {
        if (stop == DocumentationScribeInvocationStopReason.None) stop = reason;
        return false;
    }
}
