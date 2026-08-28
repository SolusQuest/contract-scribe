namespace ContractScribe.Core;

internal enum CampaignProviderCompletionKind
{
    Ordinary,
    ProposalInvalid,
    HostFailure,
    CallerCancelled,
    ShutdownCancelled,
    Timeout,
    BudgetExhausted,
}

internal sealed class CampaignProviderCompletionRegistrar
{
    private readonly CampaignProviderInvocationAuthority invocation;
    private int registrationGrant = 1;

    internal CampaignProviderCompletionRegistrar(CampaignProviderInvocationAuthority invocation) =>
        this.invocation = invocation;

    internal bool TryAuthorizePreparation(
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

    internal bool TryRegister(
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
        if (!Enum.IsDefined(kind)
            || activeElapsedMilliseconds is < 0 or > CampaignStateContract.MaximumObservation
            || outcome is null != activeElapsedMilliseconds is null)
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
            if (outcome is null || !invocation.ValidateOutcome(outcome))
            {
                return false;
            }

            return dispatched || AvailableOrdinaryOutcome(outcome);
        }

        if (!dispatched)
        {
            return outcome is null
                && kind is CampaignProviderCompletionKind.ProposalInvalid
                    or CampaignProviderCompletionKind.HostFailure;
        }

        return outcome is null
            || outcome.RunResult.Terminal is DocumentationScribeProposalTerminal
                && invocation.ValidateOutcome(outcome);
    }

    private static bool AvailableOrdinaryOutcome(DocumentationScribeValidatedRunOutcome outcome)
    {
        if (outcome.RunResult.RunEnvelope.ProviderRequestCount != 0)
        {
            return false;
        }

        return outcome.RunResult.Terminal switch
        {
            DocumentationScribeCancelledTerminal => true,
            DocumentationScribeFailureTerminal
            {
                Code: DocumentationScribeFailureCode.Validation
                    or DocumentationScribeFailureCode.Internal
                    or DocumentationScribeFailureCode.Timeout
                    or DocumentationScribeFailureCode.Budget,
            } => true,
            _ => false,
        };
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
