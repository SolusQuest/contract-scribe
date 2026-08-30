using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record DocumentationCampaignProposalInput(
    ClassifiedRepositorySession Session,
    ObservedRepositorySession Observations,
    PolicyDocumentV1 AcceptedPolicy,
    ImmutableArray<AuditRecordInput> AcceptedAuditInputs,
    AuditDocument AcceptedAuditDocument,
    CampaignPlanningInput PlanningInput,
    CampaignWorkPlan AcceptedPlan,
    CampaignScribeExecutionCapability ExecutionCapability,
    string StyleConfigurationId,
    JsonElement StyleConfigurationProjection,
    ReadOnlyMemory<byte> RequestUtf8Json,
    ICampaignCheckpointStore Store,
    DocumentationScribeRuntimeOptions RuntimeOptions,
    IDocumentationScribeModelExchange? Exchange,
    string? ConfiguredAgentEntrypoint,
    CancellationToken ExecutionToken,
    CancellationToken SettlementToken,
    TimeProvider? TimeProvider = null,
    Func<IDocumentationScribeModelExchange?>? DeferredExchange = null);

internal static class DocumentationCampaignProposalExecutor
{
    internal static async Task<DocumentationCampaignProposalOutcome> ExecuteAsync(
        DocumentationCampaignProposalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var accepted = await CampaignCheckpointAcceptance.AcceptCurrentAsync(
            input.Store, input.SettlementToken).ConfigureAwait(false);
        if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted || accepted.AcceptedCheckpoint is null)
        {
            return new DocumentationCampaignProposalOutcome(
                DocumentationCampaignProposalOutcomeKind.StateConflict,
                "campaign.checkpoint.unaccepted",
                checkpointFailure: accepted.Kind);
        }

        var current = accepted.AcceptedCheckpoint;
        DocumentationScribeAuditAuthority auditAuthority;
        try
        {
            if (!ReferenceEquals(input.Session.Classification.ClassificationSet, input.PlanningInput.Classifications)
                || !ReferenceEquals(input.Observations.ObservationSet, input.PlanningInput.Observations))
            {
                return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.session-mismatch");
            }
            CampaignStateFactory.ValidateCurrentContext(
                current.Artifact.State, input.ExecutionCapability, input.StyleConfigurationId,
                input.StyleConfigurationProjection, input.Session.RepositorySession.InputIdentity,
                input.PlanningInput, input.AcceptedPlan);
            auditAuthority = DocumentationScribeAuditAuthority.Create(
                input.Session, input.Observations, input.AcceptedPolicy,
                input.AcceptedAuditInputs, input.AcceptedAuditDocument);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.context.invalid");
        }

        var state = current.Artifact.State;
        if (state.ActiveReservation is CampaignPatchReservation)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.patch-active");
        }
        CampaignWorkItemState? selectedState = null;
        CampaignPlanningWorkItem? selectedPlan = null;
        foreach (var pair in state.WorkItems.Zip(input.AcceptedPlan.WorkItems))
        {
            var work = pair.First;
            if (work.Status == CampaignWorkStatus.ProposalComplete)
            {
                if (!TrySelectAudit(auditAuthority, input.PlanningInput, pair.Second, out _))
                {
                    return Outcome(
                        DocumentationCampaignProposalOutcomeKind.HostContractError,
                        "campaign.context.audit-invalid");
                }

                return new(DocumentationCampaignProposalOutcomeKind.ProposalReady,
                    "campaign.proposal.replay", work.WorkItemKey, current.Artifact);
            }
            if (work.Status is CampaignWorkStatus.Accepted
                || work.Status == CampaignWorkStatus.Closed
                    && work.ClosedOutcome is not
                    {
                        Code: CampaignWorkOutcomeCode.ProviderFailure,
                        ProviderDisposition: CampaignProviderFinalDisposition.Retryable
                    })
            {
                continue;
            }
            if (state.TerminalOutcome is null)
            {
                selectedState = work;
                selectedPlan = pair.Second;
                break;
            }
        }

        if (selectedState is null || selectedPlan is null)
        {
            return FromTerminal(current.Artifact);
        }
        if (state.ActiveReservation is CampaignProviderReservation active
            && active.WorkItemKey != selectedState.WorkItemKey)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.reservation.foreign");
        }

        if (!TrySelectAudit(auditAuthority, input.PlanningInput, selectedPlan, out var selectedAudit))
        {
            return Outcome(
                DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.context.audit-invalid");
        }

        if (input.RequestUtf8Json.Length > DocumentationScribeContract.MaximumArtifactUtf8Bytes)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.request.invalid");
        }

        var ownedRequestUtf8Json = input.RequestUtf8Json.ToArray().AsMemory();
        var parsed = DocumentationScribeValidation.ParseRequest(ownedRequestUtf8Json);
        if (!parsed.IsValid || parsed.Request is not { } request)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.request.invalid");
        }
        var projection = input.ExecutionCapability.PersistedProjection;
        if (input.RuntimeOptions.ProviderConfigurationId != projection.ProviderConfigurationId
            || input.RuntimeOptions.ModelConfigurationId != projection.ModelConfigurationId
            || input.RuntimeOptions.ScribeProtocolId != projection.ScribeProtocolId)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.runtime.mismatch");
        }

        var transition = selectedState.Status == CampaignWorkStatus.Planned && state.ActiveReservation is null
            ? CampaignStateReducer.AdmitProviderInvocation(current.Artifact, input.ExecutionCapability,
                input.StyleConfigurationId, input.StyleConfigurationProjection, input.PlanningInput,
                input.AcceptedPlan, selectedState.WorkItemKey, request)
            : CampaignStateReducer.RetryProviderInvocation(current.Artifact,
                state.ActiveReservation is null ? null : current, input.ExecutionCapability,
                input.StyleConfigurationId, input.StyleConfigurationProjection, input.PlanningInput,
                input.AcceptedPlan, selectedState.WorkItemKey, request);
        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.reservation.invalid");
        }

        var exchange = input.Exchange ?? input.DeferredExchange?.Invoke();
        if (exchange is null)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.credential.invalid");
        }
        using var deferredExchange = input.Exchange is null
            ? exchange as IDisposable
            : null;
        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        CampaignCheckpointAcceptanceResult reserved;
        using (CampaignProcessBoundaryHooks.EnterReplacementScope(
                   CampaignProcessBoundaryHooks.ProposalReservationReplacementScope))
        {
            reserved = await CampaignCheckpointAcceptance.AcceptAsync(
                input.Store, transition, input.SettlementToken).ConfigureAwait(false);
        }
        if (reserved.Kind != CampaignCheckpointAcceptanceKind.Accepted || reserved.AcceptedCheckpoint is null)
        {
            var failure = reserved.Kind is CampaignCheckpointAcceptanceKind.Conflict
                or CampaignCheckpointAcceptanceKind.InvalidRead
                or CampaignCheckpointAcceptanceKind.Unreadable
                ? Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict")
                : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed");
            return new DocumentationCampaignProposalOutcome(
                failure.Kind, failure.Code, checkpointFailure: reserved.Kind);
        }
        if (reserved.Artifact?.State.ActiveReservation is not CampaignProviderReservation)
        {
            return reserved.Artifact is null
                ? Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed")
                : FromArtifact(reserved.Artifact, selectedState.WorkItemKey);
        }

        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterReservationReadback);

        CampaignProviderInvocationAuthority invocation;
        try
        {
            invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
                reserved.AcceptedCheckpoint, input.ExecutionCapability, input.StyleConfigurationId,
                input.StyleConfigurationProjection, input.PlanningInput, input.AcceptedPlan, request);
        }
        catch (ArgumentException)
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.observer");
        }

        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalBeforeProviderDispatch);
        var prepared = await DocumentationScribeComposition.PrepareCampaignAsync(
            selectedAudit!, ownedRequestUtf8Json, invocation, input.ConfiguredAgentEntrypoint,
            input.RuntimeOptions, exchange, input.TimeProvider, input.ExecutionToken).ConfigureAwait(false);
        CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeResultTransition);
        CampaignTransitionResult completed;
        var proposalResult = prepared.Kind == DocumentationCampaignPreparationKind.Completion;
        CampaignProcessBoundaryHooks.Reach(proposalResult
            ? CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeProposalTransition
            : CampaignProcessBoundaryHooks.ProposalAfterProviderBeforeClosedTransition);
        if (prepared.Kind == DocumentationCampaignPreparationKind.Completion && prepared.CompletionAuthority is not null)
        {
            completed = CampaignStateReducer.CompleteProviderInvocation(
                reserved.AcceptedCheckpoint.Artifact, prepared.CompletionAuthority,
                input.ExecutionCapability, input.StyleConfigurationId, input.StyleConfigurationProjection,
                input.PlanningInput, input.AcceptedPlan);
        }
        else if (prepared.Kind is DocumentationCampaignPreparationKind.StopCancelled
            or DocumentationCampaignPreparationKind.StopTimedOut
            or DocumentationCampaignPreparationKind.StopBudgetExhausted)
        {
            var stop = prepared.Kind switch
            {
                DocumentationCampaignPreparationKind.StopCancelled => CampaignTerminalKind.Cancelled,
                DocumentationCampaignPreparationKind.StopTimedOut => CampaignTerminalKind.Timeout,
                _ => CampaignTerminalKind.Exhausted,
            };
            completed = CampaignStateReducer.StopActiveInvocation(
                reserved.AcceptedCheckpoint.Artifact, reserved.AcceptedCheckpoint, stop);
        }
        else
        {
            return Outcome(DocumentationCampaignProposalOutcomeKind.HostContractError, "campaign.preparation.invalid");
        }
        if (completed.Kind == CampaignTransitionKind.Rejected)
        {
            return Outcome(
                DocumentationCampaignProposalOutcomeKind.HostContractError,
                "campaign.settlement.invalid");
        }

        CampaignCheckpointAcceptanceResult settled;
        using (CampaignProcessBoundaryHooks.EnterReplacementScope(proposalResult
                   ? CampaignProcessBoundaryHooks.ProposalResultReplacementScope
                   : CampaignProcessBoundaryHooks.ProposalClosedReplacementScope))
        {
            settled = await CampaignCheckpointAcceptance.AcceptAsync(
                input.Store, completed, input.SettlementToken).ConfigureAwait(false);
        }
        if (settled.Kind == CampaignCheckpointAcceptanceKind.Accepted && settled.Artifact is not null)
        {
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalAfterResultReadback);
            CampaignProcessBoundaryHooks.Reach(proposalResult
                ? CampaignProcessBoundaryHooks.ProposalAfterProposalReadback
                : CampaignProcessBoundaryHooks.ProposalAfterClosedReadback);
            return FromArtifact(settled.Artifact, selectedState.WorkItemKey);
        }

        var settlementFailure = settled.Kind is CampaignCheckpointAcceptanceKind.Conflict
            or CampaignCheckpointAcceptanceKind.InvalidRead
            or CampaignCheckpointAcceptanceKind.Unreadable
            or CampaignCheckpointAcceptanceKind.WriteRejected
            ? Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.settlement.conflict")
            : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.settlement.unconfirmed");
        return new DocumentationCampaignProposalOutcome(
            settlementFailure.Kind, settlementFailure.Code, checkpointFailure: settled.Kind);
    }

    private static bool TrySelectAudit(
        DocumentationScribeAuditAuthority auditAuthority,
        CampaignPlanningInput planningInput,
        CampaignPlanningWorkItem planWork,
        out DocumentationScribeSelectedAudit? selectedAudit)
    {
        selectedAudit = null;
        try
        {
            var targetFact = planWork.Targets.Single();
            var target = planningInput.Classifications.Targets.Single(candidate =>
                candidate.SymbolRef == targetFact.SymbolRef);
            selectedAudit = auditAuthority.Select(target);
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static DocumentationCampaignProposalOutcome FromArtifact(CampaignCheckpointArtifact artifact, string workKey)
    {
        var work = artifact.State.WorkItems.Single(item => item.WorkItemKey == workKey);
        if (work.Status == CampaignWorkStatus.ProposalComplete)
            return new(DocumentationCampaignProposalOutcomeKind.ProposalReady, "campaign.proposal.ready", workKey, artifact);
        if (work.ClosedOutcome is
            {
                Code: CampaignWorkOutcomeCode.ProviderFailure,
                ProviderDisposition: CampaignProviderFinalDisposition.Retryable
            } && artifact.State.TerminalOutcome is null)
            return new(DocumentationCampaignProposalOutcomeKind.RetryableStop, "campaign.provider.retryable", workKey, artifact);
        return FromTerminal(artifact);
    }

    private static DocumentationCampaignProposalOutcome FromTerminal(CampaignCheckpointArtifact artifact) =>
        artifact.State.TerminalOutcome switch
        {
            { Kind: CampaignTerminalKind.Cancelled } => new(DocumentationCampaignProposalOutcomeKind.Cancelled, "campaign.cancelled", artifact: artifact),
            { Kind: CampaignTerminalKind.Timeout } => new(DocumentationCampaignProposalOutcomeKind.TimedOut, "campaign.timed-out", artifact: artifact),
            { Kind: CampaignTerminalKind.Exhausted } => new(DocumentationCampaignProposalOutcomeKind.BudgetExhausted, "campaign.exhausted", artifact: artifact),
            { Reason: CampaignTerminalReason.NoWork } => new(DocumentationCampaignProposalOutcomeKind.NoWork, "campaign.no-work", artifact: artifact),
            { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed }
                when artifact.State.WorkItems.All(item => item.ClosedOutcome?.Stage == CampaignWorkOutcomeStage.Planning) =>
                new(DocumentationCampaignProposalOutcomeKind.UnsupportedOnly, "campaign.unsupported-only", artifact: artifact),
            _ => new(DocumentationCampaignProposalOutcomeKind.TerminalStop, "campaign.terminal", artifact: artifact),
        };

    private static DocumentationCampaignProposalOutcome Outcome(
        DocumentationCampaignProposalOutcomeKind kind, string code) => new(kind, code);

}
