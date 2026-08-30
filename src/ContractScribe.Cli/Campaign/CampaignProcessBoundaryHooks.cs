namespace ContractScribe.Cli;

/// <summary>
/// Test-only process interruption seam. Production has no registration and the
/// single branch below is inert. A runtime startup hook may register by
/// reflection without adding an argv, configuration, or environment authority
/// to the campaign command.
/// </summary>
internal static class CampaignProcessBoundaryHooks
{
    internal const string InitialBeforeCreate = "checkpoint.initial.before-create";
    internal const string ProposalBeforeReservationCommit = "proposal.before-reservation-commit";
    internal const string ProposalAfterReservationReadback = "proposal.after-reservation-readback";
    internal const string ProposalBeforeProviderDispatch = "proposal.before-provider-dispatch";
    internal const string ProposalDuringProviderDispatch = "proposal.during-provider-dispatch";
    internal const string ProposalAfterProviderBeforeResultTransition = "proposal.after-provider-before-result-transition";
    internal const string ProposalAfterResultReadback = "proposal.after-result-readback";
    internal const string PatchBeforeReservationCommit = "patch.before-reservation-commit";
    internal const string PatchAfterReservationReadback = "patch.after-reservation-readback";
    internal const string PatchBeforeDispatch = "patch.before-dispatch";
    internal const string PatchAfterDispatchBeforeResultTransition = "patch.after-dispatch-before-result-transition";
    internal const string PatchAfterResultReadback = "patch.after-result-readback";
    internal const string CheckpointBeforeReplacement = "checkpoint.before-replacement";
    internal const string CheckpointAfterReplacementBeforeReadback = "checkpoint.after-replacement-before-readback";
    internal const string ProposalAfterProviderBeforeProposalTransition = "proposal.after-provider-before-proposal-transition";
    internal const string ProposalAfterProviderBeforeClosedTransition = "proposal.after-provider-before-closed-transition";
    internal const string ProposalAfterProposalReadback = "proposal.after-proposal-readback";
    internal const string ProposalAfterClosedReadback = "proposal.after-closed-readback";
    internal const string PatchDuringExecution = "patch.during-execution";
    internal const string PatchAfterExecutionBeforeAcceptedTransition = "patch.after-execution-before-accepted-transition";
    internal const string PatchAfterExecutionBeforeReductionTransition = "patch.after-execution-before-reduction-transition";
    internal const string PatchAfterExecutionBeforeClosedTransition = "patch.after-execution-before-closed-transition";
    internal const string PatchAfterAcceptedReadback = "patch.after-accepted-readback";
    internal const string PatchAfterReductionReadback = "patch.after-reduction-readback";
    internal const string PatchAfterClosedReadback = "patch.after-closed-readback";

    internal const string InitialReplacementScope = "checkpoint.initial";
    internal const string ProposalReservationReplacementScope = "proposal.reservation";
    internal const string ProposalResultReplacementScope = "proposal.result.proposal";
    internal const string ProposalClosedReplacementScope = "proposal.result.closed";
    internal const string PatchReservationReplacementScope = "patch.reservation";
    internal const string PatchAcceptedReplacementScope = "patch.result.accepted";
    internal const string PatchReductionReplacementScope = "patch.result.reduction";
    internal const string PatchClosedReplacementScope = "patch.result.closed";
    internal const string InReplacement = "in-replacement";
    internal const string AfterReplacementBeforeReadback = "after-replacement-before-readback";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        InitialBeforeCreate,
        ProposalBeforeReservationCommit,
        ProposalAfterReservationReadback,
        ProposalBeforeProviderDispatch,
        ProposalDuringProviderDispatch,
        ProposalAfterProviderBeforeResultTransition,
        ProposalAfterResultReadback,
        PatchBeforeReservationCommit,
        PatchAfterReservationReadback,
        PatchBeforeDispatch,
        PatchAfterDispatchBeforeResultTransition,
        PatchAfterResultReadback,
        CheckpointBeforeReplacement,
        CheckpointAfterReplacementBeforeReadback,
        ProposalAfterProviderBeforeProposalTransition,
        ProposalAfterProviderBeforeClosedTransition,
        ProposalAfterProposalReadback,
        ProposalAfterClosedReadback,
        PatchDuringExecution,
        PatchAfterExecutionBeforeAcceptedTransition,
        PatchAfterExecutionBeforeReductionTransition,
        PatchAfterExecutionBeforeClosedTransition,
        PatchAfterAcceptedReadback,
        PatchAfterReductionReadback,
        PatchAfterClosedReadback,
        InitialReplacementScope + "." + InReplacement,
        InitialReplacementScope + "." + AfterReplacementBeforeReadback,
        ProposalReservationReplacementScope + "." + InReplacement,
        ProposalReservationReplacementScope + "." + AfterReplacementBeforeReadback,
        ProposalResultReplacementScope + "." + InReplacement,
        ProposalResultReplacementScope + "." + AfterReplacementBeforeReadback,
        ProposalClosedReplacementScope + "." + InReplacement,
        ProposalClosedReplacementScope + "." + AfterReplacementBeforeReadback,
        PatchReservationReplacementScope + "." + InReplacement,
        PatchReservationReplacementScope + "." + AfterReplacementBeforeReadback,
        PatchAcceptedReplacementScope + "." + InReplacement,
        PatchAcceptedReplacementScope + "." + AfterReplacementBeforeReadback,
        PatchReductionReplacementScope + "." + InReplacement,
        PatchReductionReplacementScope + "." + AfterReplacementBeforeReadback,
        PatchClosedReplacementScope + "." + InReplacement,
        PatchClosedReplacementScope + "." + AfterReplacementBeforeReadback,
    };
    private static readonly HashSet<string> ReplacementScopes = new(StringComparer.Ordinal)
    {
        InitialReplacementScope,
        ProposalReservationReplacementScope,
        ProposalResultReplacementScope,
        ProposalClosedReplacementScope,
        PatchReservationReplacementScope,
        PatchAcceptedReplacementScope,
        PatchReductionReplacementScope,
        PatchClosedReplacementScope,
    };
    private static readonly AsyncLocal<string?> CurrentReplacementScope = new();

    private static Action<string>? observer;

    internal static IDisposable Register(Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (Interlocked.CompareExchange(ref observer, hook, null) is not null)
        {
            throw new InvalidOperationException("A campaign process-boundary hook is already registered.");
        }
        return new Registration(hook);
    }

    internal static void Reach(string name)
    {
        if (!Allowed.Contains(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }
        Volatile.Read(ref observer)?.Invoke(name);
    }

    internal static IDisposable EnterReplacementScope(string scope)
    {
        if (!ReplacementScopes.Contains(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        var prior = CurrentReplacementScope.Value;
        CurrentReplacementScope.Value = scope;
        return new ReplacementScopeRegistration(prior);
    }

    internal static void ReachReplacement(string phase)
    {
        if (phase is not (InReplacement or AfterReplacementBeforeReadback))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        if (CurrentReplacementScope.Value is { } scope)
        {
            Reach(scope + "." + phase);
        }
    }

    internal static IReadOnlyCollection<string> Allowlist => Allowed;

    private sealed class Registration(Action<string> registered) : IDisposable
    {
        private Action<string>? value = registered;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref value, null);
            if (current is not null)
            {
                Interlocked.CompareExchange(ref observer, null, current);
            }
        }
    }

    private sealed class ReplacementScopeRegistration(string? prior) : IDisposable
    {
        private string? restore = prior;
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                CurrentReplacementScope.Value = restore;
                restore = null;
            }
        }
    }
}
