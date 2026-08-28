namespace ContractScribe.Core;

public enum CampaignProviderCompletionKind
{
    Ordinary,
    ProposalInvalid,
    HostFailure,
    CallerCancelled,
    ShutdownCancelled,
    Timeout,
    BudgetExhausted,
}

public sealed class CampaignProviderCompletionRegistrar
{
    private readonly CampaignProviderInvocationAuthority invocation;
    private int registrationGrant = 1;

    internal CampaignProviderCompletionRegistrar(CampaignProviderInvocationAuthority invocation) =>
        this.invocation = invocation;

    public bool TryAuthorizePreparation(
        DocumentationScribeRequest request,
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId,
        out DocumentationScribeAttemptId attemptId)
    {
        ArgumentNullException.ThrowIfNull(request);
        attemptId = default;
        if (!invocation.ValidatePreparation(
                request,
                providerConfigurationId,
                modelConfigurationId,
                scribeProtocolId))
        {
            return false;
        }

        attemptId = invocation.AttemptId;
        return true;
    }

    public bool TryRegister(
        CampaignProviderCompletionKind kind,
        DocumentationScribeValidatedRunOutcome? outcome,
        long? activeElapsedMilliseconds,
        out CampaignProviderCompletionAuthority? authority)
    {
        authority = null;
        if (!Valid(kind, outcome, activeElapsedMilliseconds)
            || Interlocked.CompareExchange(ref registrationGrant, 0, 1) != 1)
        {
            return false;
        }

        authority = new CampaignProviderCompletionAuthority(
            invocation,
            kind,
            outcome,
            activeElapsedMilliseconds);
        return true;
    }

    private bool Valid(
        CampaignProviderCompletionKind kind,
        DocumentationScribeValidatedRunOutcome? outcome,
        long? activeElapsedMilliseconds)
    {
        if (activeElapsedMilliseconds is < 0 or > CampaignStateContract.MaximumObservation)
        {
            return false;
        }

        var dispatched = invocation.DispatchStarted;
        if (!dispatched && !invocation.LifecycleAvailable)
        {
            return false;
        }
        if (kind == CampaignProviderCompletionKind.Ordinary)
        {
            return outcome is not null
                && invocation.ValidateOutcome(outcome);
        }

        if (!dispatched && kind is not (CampaignProviderCompletionKind.ProposalInvalid
            or CampaignProviderCompletionKind.HostFailure))
        {
            return false;
        }

        return outcome is null
            || outcome.RunResult.Terminal is DocumentationScribeProposalTerminal
                && invocation.ValidateOutcome(outcome);
    }

    public override string ToString() => nameof(CampaignProviderCompletionRegistrar);
}

public sealed class CampaignProviderCompletionAuthority
{
    private int consumptionGrant = 1;

    internal CampaignProviderCompletionAuthority(
        CampaignProviderInvocationAuthority invocation,
        CampaignProviderCompletionKind kind,
        DocumentationScribeValidatedRunOutcome? outcome,
        long? activeElapsedMilliseconds)
    {
        Invocation = invocation;
        Kind = kind;
        Outcome = outcome;
        ActiveElapsedMilliseconds = activeElapsedMilliseconds;
    }

    internal CampaignProviderInvocationAuthority Invocation { get; }
    internal CampaignProviderCompletionKind Kind { get; }
    internal DocumentationScribeValidatedRunOutcome? Outcome { get; }
    internal long? ActiveElapsedMilliseconds { get; }

    internal bool TryConsume() =>
        Interlocked.CompareExchange(ref consumptionGrant, 0, 1) == 1;

    public override string ToString() => nameof(CampaignProviderCompletionAuthority);
}
