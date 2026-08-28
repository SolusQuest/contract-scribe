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
    IDocumentationScribeModelExchange Exchange,
    string? ConfiguredAgentEntrypoint,
    CancellationToken ExecutionToken,
    CancellationToken SettlementToken,
    TimeProvider? TimeProvider = null);

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
            return Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.checkpoint.unaccepted");
        }

        var current = accepted.AcceptedCheckpoint;
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

        var parsed = DocumentationScribeValidation.ParseRequest(input.RequestUtf8Json);
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
        var reserved = await CampaignCheckpointAcceptance.AcceptAsync(
            input.Store, transition, input.SettlementToken).ConfigureAwait(false);
        if (reserved.Kind != CampaignCheckpointAcceptanceKind.Accepted || reserved.AcceptedCheckpoint is null)
        {
            return reserved.Kind is CampaignCheckpointAcceptanceKind.Conflict
                or CampaignCheckpointAcceptanceKind.InvalidRead
                or CampaignCheckpointAcceptanceKind.Unreadable
                ? Outcome(DocumentationCampaignProposalOutcomeKind.StateConflict, "campaign.reservation.conflict")
                : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed");
        }
        if (reserved.Artifact?.State.ActiveReservation is not CampaignProviderReservation)
        {
            return reserved.Artifact is null
                ? Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.reservation.unconfirmed")
                : FromArtifact(reserved.Artifact, selectedState.WorkItemKey);
        }

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

        var targetFact = selectedPlan.Targets.Single();
        var target = input.PlanningInput.Classifications.Targets.Single(candidate => candidate.SymbolRef == targetFact.SymbolRef);
        var audit = DocumentationScribeAuditAuthority.Create(
            input.Session, input.Observations, input.AcceptedPolicy,
            input.AcceptedAuditInputs, input.AcceptedAuditDocument).Select(target);
        var prepared = await DocumentationScribeComposition.PrepareCampaignAsync(
            audit, input.RequestUtf8Json, invocation, input.ConfiguredAgentEntrypoint,
            input.RuntimeOptions, input.Exchange, input.TimeProvider, input.ExecutionToken).ConfigureAwait(false);
        CampaignTransitionResult completed;
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
        var settled = await CampaignCheckpointAcceptance.AcceptAsync(
            input.Store, completed, input.SettlementToken).ConfigureAwait(false);
        return settled.Kind == CampaignCheckpointAcceptanceKind.Accepted && settled.Artifact is not null
            ? FromArtifact(settled.Artifact, selectedState.WorkItemKey)
            : Outcome(DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch, "campaign.settlement.unconfirmed");
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
