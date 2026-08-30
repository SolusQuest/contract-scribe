using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

internal enum CampaignTrustedProposalAdmissionKind
{
    Admitted,
    OverBound,
    Invalid,
}

public static class CampaignStateFactory
{
    private static readonly HashSet<string> RemovablePatchDiagnosticCodes = new(StringComparer.Ordinal)
    {
        "patch.rejected.unsupported-target",
        "patch.rejected.ambiguous-target",
        "patch.rejected.non-writable-target",
        "patch.rejected.edit-state",
        "patch.rejected.unsafe-change",
    };

    internal static CampaignLineageCharges EmptyChargesForAcceptance() => EmptyCharges();

    public static CampaignStyleConfigurationAuthority CreateStyleConfigurationAuthority(
        string id,
        JsonElement validatedProjection)
    {
        if (!IsOpaqueId(id, 512) || validatedProjection.ValueKind != JsonValueKind.Object)
        {
            throw Fail(
                CampaignStateValidationCode.InvalidConfiguration,
                "Style configuration authority has an invalid shape.");
        }

        byte[] canonical;
        try
        {
            canonical = CampaignPlanningProjectionCanonicalizer.Canonicalize(validatedProjection);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Fail(
                CampaignStateValidationCode.InvalidConfiguration,
                "Style configuration authority is invalid.");
        }

        using var commitment = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-style-configuration/v1");
        commitment.Add("projection", Encoding.UTF8.GetString(canonical));
        return new CampaignStyleConfigurationAuthority(id, commitment.Complete());
    }

    public static CampaignScribeExecutionCapability CreateScribeExecutionCapability(
        CampaignPlanningExecutionPolicy policy,
        JsonElement validatedAgentProtocolProjection,
        JsonElement validatedToolPolicyAndRegistryProjection,
        JsonElement validatedProviderModelRequestProfileProjection)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateExecutionProjectionAuthority(
            policy.AgentProtocol,
            CampaignPlanningContentFamily.AgentProtocol,
            validatedAgentProtocolProjection);
        ValidateExecutionProjectionAuthority(
            policy.ToolPolicyAndRegistry,
            CampaignPlanningContentFamily.ToolPolicyAndRegistry,
            validatedToolPolicyAndRegistryProjection);
        ValidateExecutionProjectionAuthority(
            policy.ProviderModelRequestProfile,
            CampaignPlanningContentFamily.ProviderModelRequestProfile,
            validatedProviderModelRequestProfileProjection);

        var providerConfigurationId = ReadExecutionProjectionId(
            validatedProviderModelRequestProfileProjection,
            "providerConfigurationId");
        var modelConfigurationId = ReadExecutionProjectionId(
            validatedProviderModelRequestProfileProjection,
            "modelConfigurationId");
        var scribeProtocolId = ReadExecutionProjectionId(
            validatedAgentProtocolProjection,
            "scribeProtocolId");
        var toolPolicyId = ReadExecutionProjectionId(
            validatedToolPolicyAndRegistryProjection,
            "toolPolicyId");
        var agent = ProjectContentAuthority(policy.AgentProtocol);
        var tool = ProjectContentAuthority(policy.ToolPolicyAndRegistry);
        var provider = ProjectContentAuthority(policy.ProviderModelRequestProfile);
        return new CampaignScribeExecutionCapability(new CampaignScribeExecutionAuthority(
            providerConfigurationId,
            modelConfigurationId,
            scribeProtocolId,
            toolPolicyId,
            agent,
            tool,
            provider,
            CreateExecutionAuthorityCommitment(
                providerConfigurationId,
                modelConfigurationId,
                scribeProtocolId,
                toolPolicyId,
                agent,
                tool,
                provider)));
    }

    public static CampaignCheckpointState CreateInitial(
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignScribeExecutionCapability scribeExecutionCapability,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan)
    {
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(scribeExecutionCapability);

        var replanned = CampaignPlanner.Plan(planningInput);
        RequireSamePlan(replanned, acceptedPlan);
        var styleAuthority = CreateStyleConfigurationAuthority(
            styleConfigurationId,
            validatedStyleConfigurationProjection);
        var policy = planningInput.ExecutionPolicy;
        var workItems = acceptedPlan.WorkItems.Select(work => new CampaignWorkItemState(
            work.WorkItemKey,
            OuterAttemptCount: 0,
            CandidateAttemptCount: 0,
            work.Disposition.Kind == CampaignPlanningDispositionKind.Executable
                ? CampaignWorkStatus.Planned
                : CampaignWorkStatus.Closed,
            TrustedProposal: null,
            ClosedOutcome: work.Disposition.Kind == CampaignPlanningDispositionKind.Terminal
                ? new CampaignWorkClosedOutcome(
                    CampaignWorkOutcomeStage.Planning,
                    CampaignWorkOutcomeCode.PlanningTerminal,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    work.WorkItemKey)
                : null)).ToImmutableArray();
        CampaignTerminalOutcome? terminal = workItems.IsEmpty
            ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork)
            : workItems.All(item => item.Status == CampaignWorkStatus.Closed)
                ? new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.AllWorkClosed)
                : null;
        var state = new CampaignCheckpointState(
            new CampaignStateProductRevision(
                policy.ProductContractRevision.Id,
                policy.ProductContractRevision.ContentSha256),
            acceptedPlan.CampaignLineage,
            new CampaignStateSnapshotAuthority(
                planningInput.Snapshot.OpaqueSnapshotBinding,
                planningInput.Snapshot.RepositoryCommitmentSha256,
                planningInput.Snapshot.InputCommitmentSha256,
                CreateInputIdentityCommitment(inputIdentity),
                planningInput.Snapshot.PolicyAuthorityCommitmentSha256,
                planningInput.Snapshot.TargetProfile,
                acceptedPlan.ExecutionCommitment),
            checkpointRevision: 0,
            CreateCeilings(policy, styleAuthority, scribeExecutionCapability.Projection),
            EmptyCharges(),
            workItems,
            activeReservation: null,
            candidateObservation: null,
            cumulativeOutcome: null,
            knownCompletedOperations: ImmutableArray<CampaignKnownCompletedOperation>.Empty,
            terminalOutcome: terminal,
            predecessor: null);
        Validate(state);
        return state;
    }

    public static CampaignCheckpointState CreateValidated(
        CampaignStateProductRevision productRevision,
        string campaignLineage,
        CampaignStateSnapshotAuthority snapshot,
        long checkpointRevision,
        CampaignStateConfiguredCeilings configuredCeilings,
        CampaignLineageCharges lineageCharges,
        IEnumerable<CampaignWorkItemState> workItems,
        CampaignActiveReservation? activeReservation = null,
        CampaignCandidateObservation? candidateObservation = null,
        CampaignCumulativeOutcome? cumulativeOutcome = null,
        IEnumerable<CampaignKnownCompletedOperation>? knownCompletedOperations = null,
        CampaignTerminalOutcome? terminalOutcome = null,
        CampaignPredecessorSummary? predecessor = null)
    {
        ArgumentNullException.ThrowIfNull(productRevision);
        ArgumentNullException.ThrowIfNull(campaignLineage);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuredCeilings);
        ArgumentNullException.ThrowIfNull(lineageCharges);
        ArgumentNullException.ThrowIfNull(workItems);
        var boundedWorkItems = CollectBounded(
            workItems,
            CampaignStateContract.MaximumWorkItems,
            "Campaign checkpoint contains too many work items.");
        var boundedKnownCompletedOperations = CollectBounded(
            knownCompletedOperations ?? [],
            CampaignStateContract.MaximumKnownPatchCompletedOperations,
            "Campaign checkpoint contains too many known completed operations.");
        var state = new CampaignCheckpointState(
            productRevision,
            campaignLineage,
            snapshot,
            checkpointRevision,
            configuredCeilings,
            lineageCharges,
            boundedWorkItems,
            activeReservation,
            candidateObservation,
            cumulativeOutcome,
            boundedKnownCompletedOperations,
            terminalOutcome,
            predecessor);
        Validate(state);
        return state;
    }

    public static void ValidateCurrentContext(
        CampaignCheckpointState state,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        Validate(state);
        var replanned = CampaignPlanner.Plan(planningInput);
        RequireSamePlan(replanned, acceptedPlan);
        var style = CreateStyleConfigurationAuthority(
            styleConfigurationId,
            validatedStyleConfigurationProjection);
        var expectedCeilings = CreateCeilings(
            planningInput.ExecutionPolicy,
            style,
            executionCapability.Projection);
        var expectedProduct = planningInput.ExecutionPolicy.ProductContractRevision;
        var snapshot = planningInput.Snapshot;
        if (!string.Equals(state.ProductRevision.Id, expectedProduct.Id, StringComparison.Ordinal)
            || !string.Equals(
                state.ProductRevision.ContentSha256,
                expectedProduct.ContentSha256,
                StringComparison.Ordinal)
            || !string.Equals(state.CampaignLineage, acceptedPlan.CampaignLineage, StringComparison.Ordinal)
            || !string.Equals(
                state.Snapshot.OpaqueSnapshotBinding,
                snapshot.OpaqueSnapshotBinding,
                StringComparison.Ordinal)
            || !string.Equals(
                state.Snapshot.RepositoryCommitmentSha256,
                snapshot.RepositoryCommitmentSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                state.Snapshot.InputCommitmentSha256,
                snapshot.InputCommitmentSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                state.Snapshot.InputIdentityCommitmentSha256,
                CreateInputIdentityCommitment(inputIdentity),
                StringComparison.Ordinal)
            || !string.Equals(
                state.Snapshot.PolicyAuthorityCommitmentSha256,
                snapshot.PolicyAuthorityCommitmentSha256,
                StringComparison.Ordinal)
            || state.Snapshot.TargetProfile != snapshot.TargetProfile
            || state.ConfiguredCeilings != expectedCeilings
            || !string.Equals(
                state.Snapshot.ExecutionCommitmentSha256,
                acceptedPlan.ExecutionCommitment,
                StringComparison.Ordinal)
            || !state.WorkItems.Select(item => item.WorkItemKey).SequenceEqual(
                acceptedPlan.WorkItems.Select(item => item.WorkItemKey),
                StringComparer.Ordinal)
            || !state.WorkItems.Zip(acceptedPlan.WorkItems).All(pair =>
                pair.Second.Disposition.Kind switch
                {
                    CampaignPlanningDispositionKind.Terminal =>
                        pair.First.Status == CampaignWorkStatus.Closed
                        && pair.First.ClosedOutcome is
                        {
                            Stage: CampaignWorkOutcomeStage.Planning,
                            Code: CampaignWorkOutcomeCode.PlanningTerminal,
                        },
                    CampaignPlanningDispositionKind.Executable =>
                        pair.First.Status != CampaignWorkStatus.Closed
                        || pair.First.ClosedOutcome is
                        {
                            Stage: CampaignWorkOutcomeStage.Scribe or CampaignWorkOutcomeStage.Patch,
                            Code: not CampaignWorkOutcomeCode.PlanningTerminal,
                        },
                    _ => false,
                }))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Campaign checkpoint does not match the current planning authority.");
        }
    }

    public static CampaignTrustedProposal CreateTrustedProposal(
        CampaignCheckpointState state,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request,
        DocumentationScribeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var executionAuthority = executionCapability.Projection;
        _ = ValidateProviderRequestAuthority(
            state,
            executionCapability,
            styleConfigurationId,
            validatedStyleConfigurationProjection,
            planningInput,
            acceptedPlan,
            workItemKey,
            request);
        ValidateCurrentContext(
            state,
            executionCapability,
            styleConfigurationId,
            validatedStyleConfigurationProjection,
            request.Context.InputIdentity,
            planningInput,
            acceptedPlan);
        var stateWork = state.WorkItems.SingleOrDefault(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
        var planWork = acceptedPlan.WorkItems.SingleOrDefault(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
        var providerReservation = state.ActiveReservation as CampaignProviderReservation;
        var expectedScribeSource = planWork is null || planWork.Targets.Length != 1
            ? null
            : CreateScribeSourceLocator(planWork.Targets[0].Source);
        if (stateWork is null
            || planWork is null
            || providerReservation is null
            || !string.Equals(providerReservation.WorkItemKey, workItemKey, StringComparison.Ordinal)
            || !string.Equals(
                providerReservation.ScribeRequestSha256,
                request.ArtifactSha256,
                StringComparison.Ordinal)
            || providerReservation.AttemptId != result.AttemptId
            || stateWork.Status != CampaignWorkStatus.Planned
            || planWork.Disposition.Kind != CampaignPlanningDispositionKind.Executable
            || planWork.Targets.Length != 1
            || result.Terminal is not DocumentationScribeProposalTerminal terminal
            || !string.Equals(
                result.ScribeRequestSha256,
                request.ArtifactSha256,
                StringComparison.Ordinal)
            || result.AttemptId != result.RunEnvelope.AttemptId
            || request.Limits != planningInput.ExecutionPolicy.ScribeRunLimits
            || !string.Equals(request.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal)
            || !string.Equals(result.RunEnvelope.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal)
            || !string.Equals(
                result.RunEnvelope.ProviderConfigurationId,
                executionAuthority.ProviderConfigurationId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.RunEnvelope.ModelConfigurationId,
                executionAuthority.ModelConfigurationId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.RunEnvelope.ScribeProtocolId,
                executionAuthority.ScribeProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.RunEnvelope.StyleProfileId,
                request.StyleProfile.StyleProfileId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.RunEnvelope.ScribeRequestSha256,
                request.ArtifactSha256,
                StringComparison.Ordinal)
            || request.Target.SymbolRef != planWork.Targets[0].SymbolRef
            || request.Target.SourceLocator != expectedScribeSource
            || request.Context.TargetProfile != state.Snapshot.TargetProfile
            || request.Context.AuditOutcome != planWork.Targets[0].AuditOutcome
            || !string.Equals(
                request.Target.SourceSha256,
                planWork.Targets[0].Source.ContentSha256,
                StringComparison.Ordinal)
            || terminal.Target.SymbolRef != request.Target.SymbolRef
            || terminal.Target.RepositoryContextRef != request.Context.RepositoryContextRef
            || terminal.Target.SourceLocator != request.Target.SourceLocator
            || !string.Equals(
                terminal.Target.SourceSha256,
                request.Target.SourceSha256,
                StringComparison.Ordinal)
            || !request.Target.ApplicableComponents.SequenceEqual(
                planWork.Targets[0].ApplicableComponents.Select(component =>
                    new DocumentationPatchApplicableComponent(
                        MapComponentKind(component.Kind),
                        component.Identity,
                        component.Name)))
            || planWork.Targets[0].StyleProfile is null
            || !string.Equals(
                CreateStyleProfileCommitment(request.StyleProfile),
                CreateStyleProfileCommitment(planWork.Targets[0].StyleProfile!),
                StringComparison.Ordinal))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Trusted proposal does not match the current work authority.");
        }

        var source = CreatePatchLocator(planWork.Targets[0].Source);
        var editKind = planWork.Disposition.EditCapability switch
        {
            CampaignPlanningEditCapability.Insert => DocumentationPatchEditKind.Insert,
            CampaignPlanningEditCapability.Replace => DocumentationPatchEditKind.Replace,
            _ => throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Executable work has no patch edit capability."),
        };
        var referencedIds = terminal.ContentUnits
            .SelectMany(unit => unit.EvidenceReferenceIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var referencedEvidence = request.EvidenceReferences
            .Concat(result.DynamicEvidenceReferences)
            .GroupBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.Count() == 1
                ? group.Single()
                : throw Fail(
                    CampaignStateValidationCode.InvalidReference,
                    "Trusted proposal evidence identifiers are not unique."))
            .Where(item => referencedIds.Contains(item.EvidenceReferenceId, StringComparer.Ordinal))
            .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (referencedEvidence.Length != referencedIds.Length
            || referencedEvidence.Any(item =>
                item.RepositoryContextRef != request.Context.RepositoryContextRef))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidReference,
                "Trusted proposal evidence closure is incomplete.");
        }

        var allEvidence = referencedEvidence.Select(ProjectEvidence).ToImmutableArray();

        var block = new DocumentationPatchBlockRequest(
            workItemKey,
            request.Target.SymbolRef,
            source,
            editKind,
            request.Target.ApplicableComponents,
            terminal.PatchContent,
            referencedIds);
        var styleCommitment = CreateStyleProfileCommitment(request.StyleProfile);
        var proposalCommitment = CreateProposalCommitment(
            state,
            request,
            result,
            block,
            allEvidence,
            styleCommitment);
        var proposal = new CampaignTrustedProposal(
            request.ArtifactSha256,
            result.AttemptId,
            result.RunEnvelope.ProviderConfigurationId,
            result.RunEnvelope.ModelConfigurationId,
            result.RunEnvelope.ScribeProtocolId,
            block,
            allEvidence,
            styleCommitment,
            request.ToolPolicyId,
            proposalCommitment);
        ValidateProposal(state, proposal, workItemKey);
        return proposal;
    }

    internal static CampaignTrustedProposalAdmissionKind EvaluateTrustedProposalAdmission(
        CampaignCheckpointState state,
        string workItemKey,
        CampaignTrustedProposal proposal,
        DocumentationPatchContext context)
    {
        var completeProjectionSet = state.WorkItems
            .Select(item => string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                ? proposal
                : item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                    ? item.TrustedProposal
                    : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToImmutableArray();
        if (completeProjectionSet.Length > CampaignStateContract.MaximumActivePatchBlocks
            || completeProjectionSet.Length > state.ConfiguredCeilings.CampaignBudget.MaximumBlocks)
        {
            return CampaignTrustedProposalAdmissionKind.OverBound;
        }

        ImmutableArray<CampaignEvidenceProjection> catalog;
        try
        {
            catalog = ValidateProjectionEvidenceConsistency(completeProjectionSet);
        }
        catch (CampaignStateValidationException exception)
        {
            return exception.Code == CampaignStateValidationCode.InvalidBound
                ? CampaignTrustedProposalAdmissionKind.OverBound
                : CampaignTrustedProposalAdmissionKind.Invalid;
        }

        byte[] bytes;
        try
        {
            var ordered = OrderPatchProposals(completeProjectionSet);
            bytes = CampaignStateJson.WritePatchRequest(
                context,
                catalog.Select(evidence => evidence.EvidenceReferenceId).ToImmutableArray(),
                ordered.Select(item => item.PatchBlock).ToImmutableArray());
        }
        catch (CampaignStateValidationException exception)
            when (exception.Code == CampaignStateValidationCode.InvalidBound)
        {
            return CampaignTrustedProposalAdmissionKind.OverBound;
        }
        if (bytes.Length > DocumentationPatchValidator.MaximumArtifactUtf8Bytes)
        {
            return CampaignTrustedProposalAdmissionKind.OverBound;
        }

        return DocumentationPatchValidator.ParseRequest(bytes).IsValid
            ? CampaignTrustedProposalAdmissionKind.Admitted
            : CampaignTrustedProposalAdmissionKind.Invalid;
    }

    internal static void ValidateCurrentTrustedProposalAuthority(
        CampaignCheckpointState state,
        CampaignWorkItemState stateWork,
        CampaignPlanningWorkItem planWork)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateWork);
        ArgumentNullException.ThrowIfNull(planWork);
        var proposal = stateWork.TrustedProposal;
        var target = planWork.Targets.Length == 1 ? planWork.Targets[0] : null;
        var expectedEditKind = planWork.Disposition.EditCapability switch
        {
            CampaignPlanningEditCapability.Insert => DocumentationPatchEditKind.Insert,
            CampaignPlanningEditCapability.Replace => DocumentationPatchEditKind.Replace,
            _ => (DocumentationPatchEditKind?)null,
        };
        var expectedComponents = target?.ApplicableComponents.Select(component =>
            new DocumentationPatchApplicableComponent(
                MapComponentKind(component.Kind),
                component.Identity,
                component.Name));
        var expectedAttempt = stateWork.OuterAttemptCount > 0
            ? CreateScribeAttemptId(
                state.Snapshot.ExecutionCommitmentSha256,
                state.ConfiguredCeilings.ScribeExecutionAuthority,
                stateWork.WorkItemKey,
                stateWork.OuterAttemptCount)
            : (DocumentationScribeAttemptId?)null;
        if (!string.Equals(stateWork.WorkItemKey, planWork.WorkItemKey, StringComparison.Ordinal)
            || stateWork.Status is not (CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            || proposal is null
            || planWork.Disposition.Kind != CampaignPlanningDispositionKind.Executable
            || target is null
            || !target.M3Eligible
            || target.StyleProfile is null
            || expectedEditKind is null
            || expectedAttempt is null
            || proposal.HistoricalAttemptId != expectedAttempt.Value
            || !string.Equals(proposal.PatchBlock.BlockId, stateWork.WorkItemKey, StringComparison.Ordinal)
            || proposal.PatchBlock.SymbolRef != target.SymbolRef
            || proposal.PatchBlock.Locator != CreatePatchLocator(target.Source)
            || proposal.PatchBlock.EditKind != expectedEditKind.Value
            || expectedComponents is null
            || !proposal.PatchBlock.ApplicableComponents.SequenceEqual(expectedComponents)
            || !string.Equals(
                proposal.StyleProfileCommitmentSha256,
                CreateStyleProfileCommitment(target.StyleProfile),
                StringComparison.Ordinal)
            || proposal.Evidence.Any(evidence => !EvidenceSubjectBelongsToBlock(
                evidence.Subject,
                proposal.PatchBlock)))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Trusted proposal does not match the exact current work authority.");
        }
    }

    internal static DocumentationScribeAttemptId CreateScribeAttemptId(
        string executionCommitment,
        CampaignScribeExecutionAuthority authority,
        string workItemKey,
        int ordinal)
    {
        var material = string.Join('\n',
            "contract-scribe/campaign/scribe-attempt/v1",
            executionCommitment,
            authority.ProviderConfigurationId,
            authority.ModelConfigurationId,
            authority.ScribeProtocolId,
            authority.ToolPolicyId,
            workItemKey,
            ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
        if (!DocumentationScribeAttemptId.TryParse("scribe-attempt." + suffix, out var result))
        {
            throw new InvalidOperationException();
        }

        return result;
    }

    internal static CampaignTrustedProposalAdmissionKind EvaluateProviderProjectionAvailability(
        CampaignCheckpointState state,
        DocumentationPatchContext context)
    {
        var activeProposals = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        if (activeProposals.Length >= CampaignStateContract.MaximumActivePatchBlocks
            || activeProposals.Length >= state.ConfiguredCeilings.CampaignBudget.MaximumBlocks)
        {
            return CampaignTrustedProposalAdmissionKind.OverBound;
        }

        if (activeProposals.IsEmpty)
        {
            return CampaignTrustedProposalAdmissionKind.Admitted;
        }

        try
        {
            var catalog = ValidateProjectionEvidenceConsistency(activeProposals);
            var ordered = OrderPatchProposals(activeProposals);
            var bytes = CampaignStateJson.WritePatchRequest(
                context,
                catalog.Select(evidence => evidence.EvidenceReferenceId).ToImmutableArray(),
                ordered.Select(item => item.PatchBlock).ToImmutableArray());
            return bytes.Length >= DocumentationPatchValidator.MaximumArtifactUtf8Bytes
                ? CampaignTrustedProposalAdmissionKind.OverBound
                : CampaignTrustedProposalAdmissionKind.Admitted;
        }
        catch (CampaignStateValidationException exception)
        {
            return exception.Code == CampaignStateValidationCode.InvalidBound
                ? CampaignTrustedProposalAdmissionKind.OverBound
                : CampaignTrustedProposalAdmissionKind.Invalid;
        }
    }

    internal static CampaignPlanningWorkItem ValidateProviderRequestAuthority(
        CampaignCheckpointState state,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        string workItemKey,
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(request);
        var executionAuthority = executionCapability.Projection;
        ValidateExecutionAuthority(executionAuthority);
        Require(
            executionAuthority == state.ConfiguredCeilings.ScribeExecutionAuthority,
            CampaignStateValidationCode.InvalidCorrelation);
        ValidateCurrentContext(
            state,
            executionCapability,
            styleConfigurationId,
            validatedStyleConfigurationProjection,
            request.Context.InputIdentity,
            planningInput,
            acceptedPlan);
        var stateWork = state.WorkItems.SingleOrDefault(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
        var planWork = acceptedPlan.WorkItems.SingleOrDefault(item =>
            string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal));
        var target = planWork is { Targets.Length: 1 } ? planWork.Targets[0] : null;
        var expectedSource = target is null ? null : CreateScribeSourceLocator(target.Source);
        var expectedComponents = target?.ApplicableComponents.Select(component =>
            new DocumentationPatchApplicableComponent(
                MapComponentKind(component.Kind),
                component.Identity,
                component.Name));
        Require(stateWork is not null
            && planWork is not null
            && planWork.Disposition.Kind == CampaignPlanningDispositionKind.Executable
            && target is not null
            && target.M3Eligible
            && target.StyleProfile is not null
            && request.Context.TargetProfile == state.Snapshot.TargetProfile
            && request.Context.AuditOutcome == target.AuditOutcome
            && request.Target.SymbolRef == target.SymbolRef
            && request.Target.SourceLocator == expectedSource
            && string.Equals(request.Target.SourceSha256, target.Source.ContentSha256, StringComparison.Ordinal)
            && expectedComponents is not null
            && request.Target.ApplicableComponents.SequenceEqual(expectedComponents)
            && string.Equals(
                CreateStyleProfileCommitment(request.StyleProfile),
                CreateStyleProfileCommitment(target.StyleProfile),
                StringComparison.Ordinal)
            && request.Limits == planningInput.ExecutionPolicy.ScribeRunLimits
            && string.Equals(request.ToolPolicyId, executionAuthority.ToolPolicyId, StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        return planWork;
    }

    public static DocumentationPatchRequest ReconstructPatchRequest(
        CampaignCheckpointState state,
        DocumentationPatchContext context,
        IEnumerable<DocumentationScribeEvidenceReference> currentEvidence) =>
        ReconstructPatchRequestCore(
            state,
            context,
            currentEvidence,
            requireActiveReservationMatch: true);

    internal static DocumentationPatchRequest ReconstructRetriedPatchRequest(
        CampaignCheckpointState state,
        DocumentationPatchContext context,
        IEnumerable<DocumentationScribeEvidenceReference> currentEvidence) =>
        ReconstructPatchRequestCore(
            state,
            context,
            currentEvidence,
            requireActiveReservationMatch: false);

    private static DocumentationPatchRequest ReconstructPatchRequestCore(
        CampaignCheckpointState state,
        DocumentationPatchContext context,
        IEnumerable<DocumentationScribeEvidenceReference> currentEvidence,
        bool requireActiveReservationMatch)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        Validate(state);
        RequirePatchContextMatchesState(state, context);

        var activeProposals = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal
                ?? throw Fail(
                    CampaignStateValidationCode.InvalidCorrelation,
                    "Active campaign work is missing its trusted proposal."))
            .ToImmutableArray();
        Require(
            activeProposals.Length is >= 1 and <= CampaignStateContract.MaximumActivePatchBlocks,
            CampaignStateValidationCode.InvalidBound);
        var acceptedProposals = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var evidence = CollectCurrentEvidence(currentEvidence);
        ImmutableArray<CampaignTrustedProposal> proposals;
        try
        {
            ValidateCurrentEvidence(activeProposals, context, evidence);
            proposals = activeProposals;
        }
        catch (CampaignStateValidationException activeFailure)
        {
            if (acceptedProposals.IsEmpty || acceptedProposals.Length == activeProposals.Length)
            {
                throw;
            }

            try
            {
                ValidateCurrentEvidence(acceptedProposals, context, evidence);
                proposals = acceptedProposals;
            }
            catch (CampaignStateValidationException)
            {
                throw activeFailure;
            }
        }
        var request = ParsePatchRequest(context, proposals);

        if (requireActiveReservationMatch
            && state.ActiveReservation is CampaignPatchReservation reservation)
        {
            Require(
                string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal),
                CampaignStateValidationCode.InvalidCorrelation);
        }

        return request;
    }

    public static DocumentationPatchRequest ReconstructAcceptedPatchRequest(
        CampaignCheckpointState state,
        DocumentationPatchContext context,
        IEnumerable<DocumentationScribeEvidenceReference> currentEvidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        Validate(state);
        RequirePatchContextMatchesState(state, context);
        var proposals = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        Require(
            proposals.Length is >= 1 and <= CampaignStateContract.MaximumActivePatchBlocks,
            CampaignStateValidationCode.InvalidBound);
        var evidence = CollectCurrentEvidence(currentEvidence);
        ValidateCurrentEvidence(proposals, context, evidence);
        var request = ParsePatchRequest(context, proposals);
        Require(
            state.CandidateObservation is { } candidate
            && string.Equals(
                candidate.AcceptedProjectionCommitmentSha256,
                CreateAcceptedProjectionCommitment(proposals),
                StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        if (state.ActiveReservation is CampaignPatchReservation reservation)
        {
            Require(
                string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal),
                CampaignStateValidationCode.InvalidCorrelation);
        }

        return request;
    }

    public static CampaignPatchReservation CreatePatchReservation(
        CampaignCheckpointState state,
        DocumentationPatchRequest request,
        int patchAttemptCount,
        long elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        Validate(state);
        Require(state.ActiveReservation is null, CampaignStateValidationCode.InvalidCorrelation);
        _ = ResolvePatchRequestProjection(state, request);
        Require(patchAttemptCount is >= 1 and <= 1_000
            && elapsedMilliseconds is >= 0 and <= CampaignStateContract.MaximumObservation,
            CampaignStateValidationCode.InvalidBound);
        return new CampaignPatchReservation(
            request.ArtifactSha256,
            state.CheckpointRevision,
            patchAttemptCount,
            elapsedMilliseconds);
    }

    internal static void ValidatePatchRequestAuthority(
        CampaignCheckpointState state,
        DocumentationPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        Validate(state);
        _ = ResolvePatchRequestProjection(state, request);
    }

    public static CampaignPatchCompletion CreatePatchCompletion(
        CampaignCheckpointState state,
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        Validate(state);
        var reservation = state.ActiveReservation as CampaignPatchReservation;
        Require(reservation is not null
            && string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
            && reservation.ExpectedCheckpointRevision == state.CheckpointRevision,
            CampaignStateValidationCode.InvalidCorrelation);
        var proposals = ResolvePatchRequestProjection(state, request);
        var resultCommitment = CreatePatchResultCommitment(request, result);
        var kind = result.Outcome switch
        {
            DocumentationPatchOutcome.Accepted => CampaignCumulativeOutcomeKind.Accepted,
            DocumentationPatchOutcome.Rejected => CampaignCumulativeOutcomeKind.Rejected,
            DocumentationPatchOutcome.Stale => CampaignCumulativeOutcomeKind.Stale,
            _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown M2 patch outcome."),
        };
        CampaignCandidateObservation? candidate = null;
        if (result.Outcome == DocumentationPatchOutcome.Accepted)
        {
            candidate = new CampaignCandidateObservation(
                proposals.Select(proposal => proposal.PatchBlock.BlockId).ToImmutableArray(),
                CreateAcceptedProjectionCommitment(proposals),
                result.ChangedFiles.Select(file => new CampaignChangedFileObservation(
                    file.Path,
                    file.OriginalFileSha256,
                    file.CandidateFileSha256,
                    file.ChangedDocumentationBlockCount,
                    file.OriginalDocumentationByteCount,
                    file.CandidateDocumentationByteCount,
                    file.OriginalDocumentationLineCount,
                    file.CandidateDocumentationLineCount)).ToImmutableArray(),
                request.ArtifactSha256,
                resultCommitment);
        }

        return new CampaignPatchCompletion(
            candidate,
            new CampaignCumulativeOutcome(
                kind,
                request.ArtifactSha256,
                resultCommitment,
                CreateAcceptedProjectionCommitment(proposals),
                reservation!.ExpectedCheckpointRevision));
    }

    public static CampaignCumulativeOutcome CreateHostPatchOutcome(
        CampaignCheckpointState state,
        DocumentationPatchRequest request,
        CampaignCumulativeOutcomeKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        Validate(state);
        Require(kind is CampaignCumulativeOutcomeKind.HostFailure
            or CampaignCumulativeOutcomeKind.Cancelled
            or CampaignCumulativeOutcomeKind.Timeout,
            CampaignStateValidationCode.InvalidVocabulary);
        var reservation = state.ActiveReservation as CampaignPatchReservation;
        Require(reservation is not null
            && string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
            && reservation.ExpectedCheckpointRevision == state.CheckpointRevision,
            CampaignStateValidationCode.InvalidCorrelation);
        var proposals = ResolvePatchRequestProjection(state, request);
        return new CampaignCumulativeOutcome(
            kind,
            request.ArtifactSha256,
            null,
            CreateAcceptedProjectionCommitment(proposals),
            reservation!.ExpectedCheckpointRevision);
    }

    internal static bool HasKnownCompletedPatchProjection(
        CampaignCheckpointState state,
        DocumentationPatchRequest request)
    {
        var projection = ResolvePatchRequestProjection(state, request);
        var commitment = CreateAcceptedProjectionCommitment(projection);
        return state.KnownCompletedOperations.Any(operation =>
            string.Equals(operation.ProjectionCommitmentSha256, commitment, StringComparison.Ordinal));
    }

    internal static string CreateActiveProjectionCommitment(CampaignCheckpointState state)
    {
        var proposals = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        Require(!proposals.IsEmpty, CampaignStateValidationCode.InvalidCorrelation);
        return CreateAcceptedProjectionCommitment(proposals);
    }

    internal static string CreateKnownCompletedOperationBinding(
        string kind,
        string requestCommitmentSha256,
        string projectionCommitmentSha256,
        string resultCommitmentSha256)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-known-completed-operation/v1");
        writer.Add("kind", kind);
        writer.Add("request", requestCommitmentSha256);
        writer.Add("projection", projectionCommitmentSha256);
        writer.Add("result", resultCommitmentSha256);
        return writer.Complete();
    }

    public static string CreatePatchResultCommitment(
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        var validation = DocumentationPatchValidator.ValidateResult(request, result);
        if (!validation.IsValid)
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Patch result does not match its request.");
        }

        Require(
            result.Diagnostics.Length <= CampaignStateContract.MaximumDiagnostics,
            CampaignStateValidationCode.InvalidBound);

        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign/patch-result/v1");
        writer.Add("request", request.ArtifactSha256);
        writer.Add("outcome", PatchOutcomeId(result.Outcome));
        writer.Add("target.count", result.Targets.Length);
        foreach (var target in result.Targets)
        {
            writer.Add("target.block", target.BlockId);
            AddSymbol(writer, "target.symbol", target.SymbolRef);
            AddPatchLocator(writer, "target.locator", target.Locator);
            writer.Add("target.provenance.count", target.ProvenanceRefs.Length);
            foreach (var reference in target.ProvenanceRefs)
            {
                writer.Add("target.provenance", reference);
            }

            writer.Add("target.status", PatchTargetStatusId(target.Status));
        }

        writer.Add("file.count", result.ChangedFiles.Length);
        foreach (var file in result.ChangedFiles)
        {
            writer.Add("file.path", file.Path);
            writer.Add("file.original", file.OriginalFileSha256);
            writer.Add("file.candidate", file.CandidateFileSha256);
            writer.Add("file.blocks", file.ChangedDocumentationBlockCount);
            writer.Add("file.original-bytes", file.OriginalDocumentationByteCount);
            writer.Add("file.candidate-bytes", file.CandidateDocumentationByteCount);
            writer.Add("file.original-lines", file.OriginalDocumentationLineCount);
            writer.Add("file.candidate-lines", file.CandidateDocumentationLineCount);
        }

        writer.Add("changed-blocks", result.ChangedDocumentationBlockCount);
        writer.Add("invariant.count", result.Invariants.Length);
        foreach (var invariant in result.Invariants)
        {
            writer.Add("invariant.id", invariant.Id);
            writer.Add("invariant.status", PatchInvariantStatusId(invariant.Status));
        }

        writer.Add("diagnostic.count", result.Diagnostics.Length);
        foreach (var diagnostic in result.Diagnostics)
        {
            writer.Add("diagnostic.severity", PatchDiagnosticSeverityId(diagnostic.Severity));
            writer.Add("diagnostic.code", diagnostic.Code);
            writer.AddOptional("diagnostic.block", diagnostic.BlockId);
            writer.AddOptional("diagnostic.path", diagnostic.Path);
            writer.AddOptional("diagnostic.pointer", diagnostic.Pointer);
        }

        return writer.Complete();
    }

    public static CampaignPatchRejectionDecision CreatePatchRejectionReduction(
        CampaignCheckpointArtifact predecessor,
        CampaignScribeExecutionCapability executionCapability,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput planningInput,
        CampaignWorkPlan acceptedPlan,
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result,
        string workItemKey)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(executionCapability);
        ArgumentNullException.ThrowIfNull(styleConfigurationId);
        ArgumentNullException.ThrowIfNull(inputIdentity);
        ArgumentNullException.ThrowIfNull(planningInput);
        ArgumentNullException.ThrowIfNull(acceptedPlan);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workItemKey);

        try
        {
            var canonicalPredecessor = CampaignStateJson.CreateArtifact(predecessor.State);
            if (!ArtifactsEqual(canonicalPredecessor, predecessor))
            {
                return NonRemovablePatchRejection();
            }

            var state = predecessor.State;
            ValidateCurrentContext(
                state,
                executionCapability,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                planningInput,
                acceptedPlan);

            if (state.ActiveReservation is not CampaignPatchReservation reservation
                || reservation.PatchAttemptCount != 1
                || reservation.ExpectedCheckpointRevision != state.CheckpointRevision
                || !string.Equals(reservation.PatchRequestSha256, request.ArtifactSha256, StringComparison.Ordinal)
                || result.Outcome != DocumentationPatchOutcome.Rejected
                || !DocumentationPatchValidator.ValidateResult(request, result).IsValid)
            {
                return NonRemovablePatchRejection();
            }

            _ = ResolvePatchRequestProjection(state, request);
            var selectedRows = state.WorkItems
                .Where(item => string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal))
                .ToImmutableArray();
            if (selectedRows.Length != 1
                || selectedRows[0].Status != CampaignWorkStatus.ProposalComplete
                || selectedRows[0].TrustedProposal is null
                || request.Blocks.Count(block => string.Equals(block.BlockId, workItemKey, StringComparison.Ordinal)) != 1)
            {
                return NonRemovablePatchRejection();
            }

            var selectedTargets = result.Targets
                .Where(target => string.Equals(target.BlockId, workItemKey, StringComparison.Ordinal))
                .ToImmutableArray();
            if (selectedTargets.Length != 1
                || selectedTargets[0].Status != DocumentationPatchTargetStatus.Invalid
                || result.Targets.Any(target =>
                    !string.Equals(target.BlockId, workItemKey, StringComparison.Ordinal)
                    && target.Status != DocumentationPatchTargetStatus.Valid)
                || result.Invariants.Any(invariant => invariant.Status == DocumentationPatchInvariantStatus.Failed)
                || result.Diagnostics.Any(diagnostic =>
                    !string.Equals(diagnostic.BlockId, workItemKey, StringComparison.Ordinal)
                    || !RemovablePatchDiagnosticCodes.Contains(diagnostic.Code)))
            {
                return NonRemovablePatchRejection();
            }

            var planRows = acceptedPlan.WorkItems
                .ToDictionary(item => item.WorkItemKey, StringComparer.Ordinal);
            if (!planRows.TryGetValue(workItemKey, out var selectedPlanRow)
                || state.WorkItems
                    .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
                    .Any(item => !planRows.ContainsKey(item.WorkItemKey))
                || state.WorkItems
                    .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
                    .Where(item => !string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal))
                    .Select(item => planRows[item.WorkItemKey])
                    .Any(other => SharesPatchAuthority(selectedPlanRow, other)))
            {
                return NonRemovablePatchRejection();
            }

            var resultCommitment = CreatePatchResultCommitment(request, result);
            var closedOutcome = new CampaignWorkClosedOutcome(
                CampaignWorkOutcomeStage.Patch,
                CampaignWorkOutcomeCode.PatchRejected,
                null,
                null,
                null,
                request.ArtifactSha256,
                resultCommitment,
                null,
                workItemKey);
            var remainingWork = state.WorkItems.Select(item =>
                string.Equals(item.WorkItemKey, workItemKey, StringComparison.Ordinal)
                    ? item with
                    {
                        Status = CampaignWorkStatus.Closed,
                        TrustedProposal = null,
                        ClosedOutcome = closedOutcome,
                    }
                    : item).ToImmutableArray();

            var projectionCheck = new CampaignCheckpointState(
                state.ProductRevision,
                state.CampaignLineage,
                state.Snapshot,
                state.CheckpointRevision,
                state.ConfiguredCeilings,
                state.LineageCharges,
                remainingWork,
                activeReservation: null,
                state.CandidateObservation,
                state.CumulativeOutcome,
                state.KnownCompletedOperations,
                state.TerminalOutcome,
                state.Predecessor);
            Validate(projectionCheck);

            return new CampaignPatchRejectionDecision(
                CampaignPatchRejectionDecisionKind.Removable,
                new CampaignPatchRejectionReduction(
                    predecessor,
                    workItemKey,
                    request.ArtifactSha256,
                    resultCommitment,
                    closedOutcome));
        }
        catch (Exception exception) when (exception is CampaignStateValidationException
            or CampaignPlanningValidationException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return NonRemovablePatchRejection();
        }
    }

    private static CampaignPatchRejectionDecision NonRemovablePatchRejection() =>
        new(CampaignPatchRejectionDecisionKind.NonRemovable, null);

    private static bool ArtifactsEqual(
        CampaignCheckpointArtifact left,
        CampaignCheckpointArtifact right) =>
        left.CheckpointRevision == right.CheckpointRevision
        && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
        && left.ExactUtf8Json.AsSpan().SequenceEqual(right.ExactUtf8Json.AsSpan());

    private static bool SharesPatchAuthority(
        CampaignPlanningWorkItem selected,
        CampaignPlanningWorkItem other)
    {
        if (string.Equals(selected.OwnerEquivalenceRef, other.OwnerEquivalenceRef, StringComparison.Ordinal))
        {
            return true;
        }

        return selected.Targets.Any(left => other.Targets.Any(right =>
            SamePhysicalSource(left.Source, right.Source)
            && SourceAuthoritySpans(left.Source).Any(leftSpan =>
                SourceAuthoritySpans(right.Source).Any(rightSpan => SpansOverlap(leftSpan, rightSpan)))));
    }

    private static bool SamePhysicalSource(
        CampaignPlanningSourceAuthority left,
        CampaignPlanningSourceAuthority right) =>
        (left, right) switch
        {
            (CampaignPlanningRepositorySourceAuthority leftRepository,
                CampaignPlanningRepositorySourceAuthority rightRepository) =>
                string.Equals(
                    leftRepository.PhysicalSourceCommitmentSha256,
                    rightRepository.PhysicalSourceCommitmentSha256,
                    StringComparison.Ordinal),
            (CampaignPlanningGeneratedSourceAuthority leftGenerated,
                CampaignPlanningGeneratedSourceAuthority rightGenerated) =>
                leftGenerated.Kind == rightGenerated.Kind
                && string.Equals(leftGenerated.ProducerId, rightGenerated.ProducerId, StringComparison.Ordinal)
                && string.Equals(leftGenerated.OutputId, rightGenerated.OutputId, StringComparison.Ordinal),
            _ => false,
        };

    private static ImmutableArray<Utf16Span> SourceAuthoritySpans(CampaignPlanningSourceAuthority source)
    {
        var builder = ImmutableArray.CreateBuilder<Utf16Span>(5);
        builder.Add(source.OwnerSpan);
        builder.Add(source.ObservationDeclarationSpan);
        builder.Add(source.RequestedDeclarationSpan);
        builder.Add(source.CanonicalDeclarationSpan);
        if (source.DocumentationSpan is { } documentationSpan)
        {
            builder.Add(documentationSpan);
        }

        return builder.MoveToImmutable();
    }

    private static bool SpansOverlap(Utf16Span left, Utf16Span right) =>
        left.Start < right.End && right.Start < left.End;

    internal static void Validate(CampaignCheckpointState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(IsOpaqueId(state.ProductRevision.Id, CampaignStateContract.MaximumIdentifierScalars), CampaignStateValidationCode.InvalidVocabulary);
        RequireSha(state.ProductRevision.ContentSha256);
        Require(IsOpaqueId(state.CampaignLineage, CampaignStateContract.MaximumIdentifierScalars), CampaignStateValidationCode.InvalidVocabulary);
        ValidateSnapshot(state.Snapshot);
        Require(state.CheckpointRevision is >= 0 and <= CampaignStateContract.MaximumObservation,
            CampaignStateValidationCode.InvalidBound);
        ValidateCeilings(state.ConfiguredCeilings);
        ValidateCharges(state.LineageCharges);
        Require(!state.WorkItems.IsDefault, CampaignStateValidationCode.InvalidShape);
        Require(state.WorkItems.Length <= CampaignStateContract.MaximumWorkItems, CampaignStateValidationCode.InvalidBound);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var activeBlocks = 0;
        var completedOverboundScribeOperations = 0;
        for (var index = 0; index < state.WorkItems.Length; index++)
        {
            var work = state.WorkItems[index];
            Require(work is not null, CampaignStateValidationCode.InvalidShape);
            Require(IsWorkItemKey(work.WorkItemKey), CampaignStateValidationCode.InvalidVocabulary);
            Require(keys.Add(work.WorkItemKey), CampaignStateValidationCode.InvalidOrder);
            Require(work.OuterAttemptCount >= 0
                && work.OuterAttemptCount <= state.ConfiguredCeilings.CampaignBudget.MaximumAttemptsPerTarget,
                CampaignStateValidationCode.InvalidBound);
            Require(work.CandidateAttemptCount >= 0
                && work.CandidateAttemptCount <= state.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock,
                CampaignStateValidationCode.InvalidBound);
            Require(Enum.IsDefined(work.Status), CampaignStateValidationCode.InvalidVocabulary);
            var hasProposal = work.TrustedProposal is not null;
            var hasClosed = work.ClosedOutcome is not null;
            Require(work.Status switch
            {
                CampaignWorkStatus.Planned => !hasProposal && !hasClosed,
                CampaignWorkStatus.ProposalComplete => hasProposal && !hasClosed,
                CampaignWorkStatus.Accepted => hasProposal && !hasClosed,
                CampaignWorkStatus.Closed => !hasProposal && hasClosed,
                _ => false,
            }, CampaignStateValidationCode.InvalidShape);
            if (work.TrustedProposal is not null)
            {
                ValidateProposal(state, work.TrustedProposal, work.WorkItemKey);
                activeBlocks++;
            }

            if (work.ClosedOutcome is not null)
            {
                ValidateClosedOutcome(work.ClosedOutcome, work.WorkItemKey);
                if (work.ClosedOutcome is
                    {
                        Stage: CampaignWorkOutcomeStage.Scribe,
                        Code: CampaignWorkOutcomeCode.CompletedOverBound,
                    })
                {
                    completedOverboundScribeOperations++;
                }
            }
        }

        Require(completedOverboundScribeOperations <= 1, CampaignStateValidationCode.InvalidBound);
        Require(activeBlocks <= CampaignStateContract.MaximumActivePatchBlocks
            && activeBlocks <= state.ConfiguredCeilings.CampaignBudget.MaximumBlocks,
            CampaignStateValidationCode.InvalidBound);
        ValidateActiveProjectionSet(state.WorkItems);
        var activeProposals = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        if (!activeProposals.IsEmpty)
        {
            ValidateProjectionEvidenceConsistency(activeProposals);
            Require(RepositoryContextRef.TryParse(
                "repoctx-00000000000000000000000000000000",
                out var repositoryContextRef),
                CampaignStateValidationCode.InvalidConfiguration);
            _ = ParsePatchRequest(
                new DocumentationPatchContext(
                    repositoryContextRef,
                    new string('a', CampaignStateContract.MaximumPathScalars),
                    state.Snapshot.TargetProfile),
                activeProposals);
        }

        var acceptedProposals = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        if (!acceptedProposals.IsEmpty)
        {
            Require(RepositoryContextRef.TryParse(
                "repoctx-00000000000000000000000000000000",
                out var repositoryContextRef),
                CampaignStateValidationCode.InvalidConfiguration);
            _ = ParsePatchRequest(
                new DocumentationPatchContext(
                    repositoryContextRef,
                    new string('a', CampaignStateContract.MaximumPathScalars),
                    state.Snapshot.TargetProfile),
                acceptedProposals);
        }

        ValidateReservation(state);
        ValidateCandidate(state);
        ValidateCumulativeOutcome(state);
        ValidateKnownCompletedOperations(state);
        ValidateTerminal(state);
        ValidatePredecessor(state);
        CampaignStateJson.ValidateEncodedSize(state);
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static ImmutableArray<T> CollectBounded<T>(
        IEnumerable<T> values,
        int maximumItems,
        string message)
    {
        var builder = ImmutableArray.CreateBuilder<T>();
        foreach (var value in values)
        {
            if (builder.Count == maximumItems)
            {
                throw Fail(CampaignStateValidationCode.InvalidBound, message);
            }

            builder.Add(value);
        }

        return builder.ToImmutable();
    }

    private static void ValidateExecutionAuthority(CampaignScribeExecutionAuthority authority)
    {
        Require(authority is not null
            && IsOpaqueId(authority.ProviderConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
            && IsOpaqueId(authority.ModelConfigurationId, DocumentationScribeContract.MaximumIdentifierScalars)
            && IsOpaqueId(authority.ScribeProtocolId, DocumentationScribeContract.MaximumIdentifierScalars)
            && IsOpaqueId(authority.ToolPolicyId, DocumentationScribeContract.MaximumIdentifierScalars)
            && ValidContentAuthority(authority.AgentProtocolAuthority)
            && ValidContentAuthority(authority.ToolPolicyAndRegistryAuthority)
            && ValidContentAuthority(authority.ProviderModelRequestProfileAuthority)
            && IsSha256(authority.BindingCommitmentSha256)
            && string.Equals(
                authority.BindingCommitmentSha256,
                CreateExecutionAuthorityCommitment(
                    authority.ProviderConfigurationId,
                    authority.ModelConfigurationId,
                    authority.ScribeProtocolId,
                    authority.ToolPolicyId,
                    authority.AgentProtocolAuthority,
                    authority.ToolPolicyAndRegistryAuthority,
                    authority.ProviderModelRequestProfileAuthority),
                StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidVocabulary);
    }

    private static bool ValidContentAuthority(CampaignStateProductRevision? authority) =>
        authority is not null
        && IsOpaqueId(authority.Id, CampaignStateContract.MaximumIdentifierScalars)
        && IsSha256(authority.ContentSha256);

    private static void ValidateExecutionProjectionAuthority(
        CampaignPlanningContentAuthority expected,
        CampaignPlanningContentFamily family,
        JsonElement projection)
    {
        try
        {
            var actual = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
                family,
                expected.Id,
                projection);
            Require(actual == expected, CampaignStateValidationCode.InvalidConfiguration);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && exception is not CampaignStateValidationException)
        {
            throw Fail(
                CampaignStateValidationCode.InvalidConfiguration,
                "Scribe execution configuration projection does not match its planning authority.");
        }
    }

    private static string ReadExecutionProjectionId(JsonElement projection, string propertyName)
    {
        if (projection.ValueKind != JsonValueKind.Object
            || !projection.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } value
            || !IsOpaqueId(value, DocumentationScribeContract.MaximumIdentifierScalars))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidConfiguration,
                "Scribe execution configuration projection is missing a valid concrete identifier.");
        }

        return value;
    }

    private static CampaignStateProductRevision ProjectContentAuthority(
        CampaignPlanningContentAuthority authority) =>
        new(authority.Id, authority.ContentSha256);

    private static string CreateExecutionAuthorityCommitment(
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId,
        string toolPolicyId,
        CampaignStateProductRevision agent,
        CampaignStateProductRevision tool,
        CampaignStateProductRevision provider)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-scribe-execution-authority/v1");
        writer.Add("provider.id", providerConfigurationId);
        writer.Add("model.id", modelConfigurationId);
        writer.Add("protocol.id", scribeProtocolId);
        writer.Add("tool-policy.id", toolPolicyId);
        writer.Add("agent.authority.id", agent.Id);
        writer.Add("agent.authority.sha", agent.ContentSha256);
        writer.Add("tool.authority.id", tool.Id);
        writer.Add("tool.authority.sha", tool.ContentSha256);
        writer.Add("provider.authority.id", provider.Id);
        writer.Add("provider.authority.sha", provider.ContentSha256);
        return writer.Complete();
    }

    private static ImmutableArray<DocumentationScribeEvidenceReference> CollectCurrentEvidence(
        IEnumerable<DocumentationScribeEvidenceReference> currentEvidence) =>
        CollectBounded(
            currentEvidence,
            CampaignStateContract.MaximumEvidenceReferences,
            "Current evidence contains too many references.");

    private static void ValidateCurrentEvidence(
        ImmutableArray<CampaignTrustedProposal> proposals,
        DocumentationPatchContext context,
        ImmutableArray<DocumentationScribeEvidenceReference> currentEvidence)
    {
        var expected = ValidateProjectionEvidenceConsistency(proposals);
        Require(currentEvidence.Length == expected.Length,
            CampaignStateValidationCode.InvalidCorrelation);
        for (var index = 0; index < currentEvidence.Length; index++)
        {
            Require(currentEvidence[index] is not null
                && currentEvidence[index].RepositoryContextRef == context.RepositoryContextRef
                && EvidenceProjectionEquals(ProjectEvidence(currentEvidence[index]), expected[index]),
                CampaignStateValidationCode.InvalidCorrelation);
        }
    }

    private static ImmutableArray<CampaignEvidenceProjection> ValidateProjectionEvidenceConsistency(
        ImmutableArray<CampaignTrustedProposal> proposals)
    {
        var unique = new Dictionary<string, CampaignEvidenceProjection>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            foreach (var evidence in proposal.Evidence)
            {
                if (unique.TryGetValue(evidence.EvidenceReferenceId, out var prior))
                {
                    Require(EvidenceProjectionEquals(evidence, prior),
                        CampaignStateValidationCode.InvalidCorrelation);
                    continue;
                }

                Require(unique.Count < CampaignStateContract.MaximumEvidenceReferences,
                    CampaignStateValidationCode.InvalidBound);
                unique.Add(evidence.EvidenceReferenceId, evidence);
            }
        }

        return unique.Values
            .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool EvidenceProjectionEquals(
        CampaignEvidenceProjection left,
        CampaignEvidenceProjection right) =>
        string.Equals(left.EvidenceReferenceId, right.EvidenceReferenceId, StringComparison.Ordinal)
        && left.Subject == right.Subject
        && left.Kind == right.Kind
        && left.Relation == right.Relation
        && left.Authority == right.Authority
        && left.Locator == right.Locator
        && string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
        && left.OriginalUtf8ByteCount == right.OriginalUtf8ByteCount
        && left.IncludedUtf8ByteCount == right.IncludedUtf8ByteCount
        && left.IsTruncated == right.IsTruncated
        && left.ClaimCategoryIds.SequenceEqual(right.ClaimCategoryIds, StringComparer.Ordinal);

    internal static bool IsOpaqueId(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!(character is >= 'a' and <= 'z'
                || character is >= 'A' and <= 'Z'
                || character is >= '0' and <= '9'
                || index > 0 && character is '.' or '_' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsCanonicalRepositoryPath(string? path) =>
        DocumentationScribeValidation.IsStableRepositoryRelativePath(path);

    internal static CampaignStateValidationException Fail(
        CampaignStateValidationCode code,
        string message) => new(code, message);

    private static CampaignStateConfiguredCeilings CreateCeilings(
        CampaignPlanningExecutionPolicy policy,
        CampaignStyleConfigurationAuthority style,
        CampaignScribeExecutionAuthority executionAuthority)
    {
        ValidateExecutionAuthority(executionAuthority);
        Require(
            executionAuthority.AgentProtocolAuthority == ProjectContentAuthority(policy.AgentProtocol)
            && executionAuthority.ToolPolicyAndRegistryAuthority == ProjectContentAuthority(policy.ToolPolicyAndRegistry)
            && executionAuthority.ProviderModelRequestProfileAuthority
                == ProjectContentAuthority(policy.ProviderModelRequestProfile),
            CampaignStateValidationCode.InvalidConfiguration);
        var budget = policy.CampaignBudget;
        var limits = policy.ScribeRunLimits;
        var projectedBudget = new CampaignStateCampaignBudget(
            budget.MaximumBlocks,
            budget.MaximumChangedFiles,
            budget.MaximumPatchBytes,
            budget.MaximumProviderRequests,
            budget.MaximumAttemptsPerTarget,
            budget.MaximumInputTokens,
            budget.MaximumUncachedInputTokens,
            budget.MaximumOutputTokens,
            budget.MaximumCostMicrounits,
            budget.MaximumElapsedMilliseconds,
            budget.MaximumCandidatesPerBlock,
            budget.CostEnforced,
            budget.CostCurrency,
            budget.CostRatePolicy?.Id,
            budget.CostRatePolicy?.ContentSha256);
        var projectedLimits = new CampaignStateScribeLimits(
            limits.MaximumContextReferences,
            limits.MaximumContextUtf8Bytes,
            limits.MaximumEvidenceReferences,
            limits.MaximumEvidenceUtf8Bytes,
            limits.MaximumProviderRequests,
            limits.MaximumToolRounds,
            limits.MaximumToolCalls,
            limits.MaximumAttempts,
            limits.MaximumInputTokens,
            limits.MaximumUncachedInputTokens,
            limits.MaximumOutputTokens,
            limits.MaximumCostMicrounits,
            limits.MaximumElapsedMilliseconds);
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-configuration/v1");
        writer.Add("style.id", style.Id);
        writer.Add("style.sha", style.ContentSha256);
        AddContentAuthority(writer, "proposal", policy.ProposalContract);
        AddContentAuthority(writer, "agent", policy.AgentProtocol);
        AddContentAuthority(writer, "context", policy.ContextSelectionPolicy);
        AddContentAuthority(writer, "tool", policy.ToolPolicyAndRegistry);
        AddContentAuthority(writer, "provider", policy.ProviderModelRequestProfile);
        AddContentAuthority(writer, "retry", policy.RetryPolicy);
        AddContentAuthority(writer, "m2", policy.M2ProjectionPolicy);
        writer.Add("scribe-execution", executionAuthority.BindingCommitmentSha256);
        writer.Add("cost.enforced", budget.CostEnforced);
        writer.AddOptional("cost.currency", budget.CostCurrency);
        writer.AddOptional("cost.rate.id", budget.CostRatePolicy?.Id);
        writer.AddOptional("cost.rate.sha", budget.CostRatePolicy?.ContentSha256);
        AddBudget(writer, projectedBudget);
        AddLimits(writer, projectedLimits);
        return new CampaignStateConfiguredCeilings(
            projectedBudget,
            projectedLimits,
            executionAuthority,
            style,
            writer.Complete());
    }

    private static CampaignLineageCharges EmptyCharges()
    {
        var zero = new CampaignChargeObservation(0, 0, 0);
        return new CampaignLineageCharges(0, zero, zero, zero, zero, zero, zero, zero, zero, 0);
    }

    private static void RequireSamePlan(CampaignWorkPlan actual, CampaignWorkPlan accepted)
    {
        if (!string.Equals(actual.CampaignLineage, accepted.CampaignLineage, StringComparison.Ordinal)
            || !string.Equals(actual.OpaqueSnapshotBinding, accepted.OpaqueSnapshotBinding, StringComparison.Ordinal)
            || !string.Equals(actual.AuditDocumentSha256, accepted.AuditDocumentSha256, StringComparison.Ordinal)
            || !string.Equals(actual.ExecutionCommitment, accepted.ExecutionCommitment, StringComparison.Ordinal)
            || actual.TargetProfile != accepted.TargetProfile
            || !SameSummary(actual.Summary, accepted.Summary)
            || !actual.WorkItems.Select(item => item.WorkItemKey).SequenceEqual(
                accepted.WorkItems.Select(item => item.WorkItemKey),
                StringComparer.Ordinal)
            || !actual.WorkItems.Select(item => item.Disposition)
                .Zip(accepted.WorkItems.Select(item => item.Disposition))
                .All(pair => SameDisposition(pair.First, pair.Second)))
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Accepted campaign plan does not match a fresh deterministic replan.");
        }
    }

    private static bool SameSummary(
        CampaignPlanningSummary actual,
        CampaignPlanningSummary accepted) =>
        actual.TotalWorkItems == accepted.TotalWorkItems
        && actual.ExecutableWorkItems == accepted.ExecutableWorkItems
        && actual.TerminalWorkItems == accepted.TerminalWorkItems
        && actual.RepositoryBackedWorkItems == accepted.RepositoryBackedWorkItems
        && actual.GeneratedOrNonWritableWorkItems == accepted.GeneratedOrNonWritableWorkItems
        && actual.TerminalReasonCounts.SequenceEqual(accepted.TerminalReasonCounts);

    private static bool SameDisposition(
        CampaignPlanningDisposition actual,
        CampaignPlanningDisposition accepted) =>
        actual.Kind == accepted.Kind
        && actual.EditCapability == accepted.EditCapability
        && actual.PrimaryTerminalReason == accepted.PrimaryTerminalReason
        && actual.TerminalReasons.SequenceEqual(accepted.TerminalReasons);

    private static void ValidateSnapshot(CampaignStateSnapshotAuthority snapshot)
    {
        Require(snapshot is not null, CampaignStateValidationCode.InvalidShape);
        Require(IsOpaqueId(snapshot.OpaqueSnapshotBinding, 512), CampaignStateValidationCode.InvalidVocabulary);
        RequireSha(snapshot.RepositoryCommitmentSha256);
        RequireSha(snapshot.InputCommitmentSha256);
        RequireSha(snapshot.InputIdentityCommitmentSha256);
        RequireSha(snapshot.PolicyAuthorityCommitmentSha256);
        Require(Enum.IsDefined(snapshot.TargetProfile), CampaignStateValidationCode.InvalidVocabulary);
        RequireSha(snapshot.ExecutionCommitmentSha256);
    }

    private static void ValidateCeilings(CampaignStateConfiguredCeilings ceilings)
    {
        Require(ceilings is not null
            && ceilings.CampaignBudget is not null
            && ceilings.ScribeRunLimits is not null
            && ceilings.ScribeExecutionAuthority is not null
            && ceilings.StyleConfigurationAuthority is not null,
            CampaignStateValidationCode.InvalidShape);
        var budget = ceilings.CampaignBudget;
        Require(budget.MaximumBlocks is >= 0 and <= CampaignStateContract.MaximumWorkItems
            && budget.MaximumChangedFiles is >= 0 and <= CampaignStateContract.MaximumChangedFiles
            && budget.MaximumPatchBytes is >= 0 and <= CampaignStateContract.MaximumPatchBytes
            && budget.MaximumProviderRequests is >= 0 and <= 1_000_000
            && budget.MaximumAttemptsPerTarget is >= 0 and <= 1_000
            && budget.MaximumInputTokens is >= 0 and <= CampaignStateContract.MaximumCampaignInputTokens
            && budget.MaximumUncachedInputTokens is >= 0
            && budget.MaximumUncachedInputTokens <= budget.MaximumInputTokens
            && budget.MaximumOutputTokens is >= 0 and <= CampaignStateContract.MaximumCampaignInputTokens
            && budget.MaximumCostMicrounits is >= 0 and <= CampaignStateContract.MaximumObservation
            && budget.MaximumElapsedMilliseconds is >= 0 and <= CampaignStateContract.MaximumCampaignElapsedMilliseconds
            && budget.MaximumCandidatesPerBlock is >= 0 and <= 1_000,
            CampaignStateValidationCode.InvalidBound);
        Require(budget.CostEnforced
            ? IsOpaqueId(budget.CostCurrency, 32)
                && IsOpaqueId(budget.CostRatePolicyId, 512)
                && IsSha256(budget.CostRatePolicySha256)
            : budget.CostCurrency is null
                && budget.CostRatePolicyId is null
                && budget.CostRatePolicySha256 is null,
            CampaignStateValidationCode.InvalidConfiguration);
        var limits = ceilings.ScribeRunLimits;
        Require(limits.MaximumContextReferences is >= 0 and <= DocumentationScribeContract.MaximumReferences
            && limits.MaximumContextUtf8Bytes is >= 0 and <= 4_194_304
            && limits.MaximumEvidenceReferences is >= 0 and <= DocumentationScribeContract.MaximumReferences
            && limits.MaximumEvidenceUtf8Bytes is >= 0 and <= 4_194_304
            && limits.MaximumProviderRequests is >= 1 and <= 128
            && limits.MaximumToolRounds is >= 0 and <= 128
            && limits.MaximumToolCalls is >= 0 and <= 1_024
            && limits.MaximumAttempts is >= 1 and <= DocumentationScribeContract.MaximumAttempts
            && limits.MaximumInputTokens is >= 1 and <= DocumentationScribeContract.MaximumConfiguredInputTokens
            && limits.MaximumUncachedInputTokens >= 0
            && limits.MaximumUncachedInputTokens <= limits.MaximumInputTokens
            && limits.MaximumOutputTokens is >= 1 and <= DocumentationScribeContract.MaximumConfiguredOutputTokens
            && limits.MaximumCostMicrounits is >= 0 and <= DocumentationScribeContract.MaximumConfiguredCostMicrounits
            && limits.MaximumElapsedMilliseconds is >= 1 and <= DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds,
            CampaignStateValidationCode.InvalidBound);
        Require(IsOpaqueId(ceilings.StyleConfigurationAuthority.Id, 512), CampaignStateValidationCode.InvalidVocabulary);
        RequireSha(ceilings.StyleConfigurationAuthority.ContentSha256);
        ValidateExecutionAuthority(ceilings.ScribeExecutionAuthority);
        RequireSha(ceilings.CampaignConfigurationCommitmentSha256);
    }

    private static void ValidateCharges(CampaignLineageCharges charges)
    {
        Require(charges is not null
            && charges.OuterInvocations is >= 0 and <= CampaignStateContract.MaximumObservation
            && charges.PatchValidationInvocations is >= 0 and <= CampaignStateContract.MaximumObservation,
            CampaignStateValidationCode.InvalidBound);
        ValidateCharge(charges.ProviderRequests);
        ValidateCharge(charges.InputTokens);
        ValidateCharge(charges.CachedInputTokens);
        ValidateCharge(charges.UncachedInputTokens);
        ValidateCharge(charges.OutputTokens);
        ValidateCharge(charges.ReasoningTokens);
        ValidateCharge(charges.CostMicrounits);
        ValidateCharge(charges.ActiveElapsedMilliseconds);
    }

    private static void ValidateCharge(CampaignChargeObservation charge)
    {
        Require(charge is not null
            && (charge.Observed is null || charge.Observed is >= 0 and <= CampaignStateContract.MaximumObservation)
            && charge.ConservativeUnobserved is >= 0 and <= CampaignStateContract.MaximumObservation
            && charge.TotalCharged is >= 0 and <= CampaignStateContract.MaximumObservation,
            CampaignStateValidationCode.InvalidBound);
        long expected;
        try
        {
            expected = checked((charge.Observed ?? 0) + charge.ConservativeUnobserved);
        }
        catch (OverflowException)
        {
            throw Fail(CampaignStateValidationCode.InvalidBound, "Campaign charge is out of range.");
        }

        Require(expected == charge.TotalCharged, CampaignStateValidationCode.InvalidCorrelation);
    }

    private static void ValidateProposal(
        CampaignCheckpointState state,
        CampaignTrustedProposal proposal,
        string workItemKey)
    {
        RequireSha(proposal.HistoricalScribeRequestSha256);
        Require(DocumentationScribeAttemptId.TryParse(proposal.HistoricalAttemptId.Value, out _), CampaignStateValidationCode.InvalidVocabulary);
        var executionAuthority = state.ConfiguredCeilings.ScribeExecutionAuthority;
        ValidateExecutionAuthority(executionAuthority);
        Require(
            string.Equals(
                proposal.ProviderConfigurationId,
                executionAuthority.ProviderConfigurationId,
                StringComparison.Ordinal)
            && string.Equals(
                proposal.ModelConfigurationId,
                executionAuthority.ModelConfigurationId,
                StringComparison.Ordinal)
            && string.Equals(
                proposal.ScribeProtocolId,
                executionAuthority.ScribeProtocolId,
                StringComparison.Ordinal)
            && string.Equals(
                proposal.ToolPolicyId,
                executionAuthority.ToolPolicyId,
                StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        Require(proposal.PatchBlock is not null
            && string.Equals(proposal.PatchBlock.BlockId, workItemKey, StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        Require(!proposal.Evidence.IsDefault
            && proposal.Evidence.Length <= CampaignStateContract.MaximumEvidenceReferences,
            CampaignStateValidationCode.InvalidBound);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousId = null;
        foreach (var evidence in proposal.Evidence)
        {
            Require(evidence is not null
                && evidenceIds.Add(evidence.EvidenceReferenceId)
                && (previousId is null || string.CompareOrdinal(previousId, evidence.EvidenceReferenceId) < 0)
                && DocumentationScribeValidation.IsStableEvidenceProjection(
                    evidence.EvidenceReferenceId,
                    evidence.Subject,
                    evidence.Kind,
                    evidence.Relation,
                    evidence.Authority,
                    evidence.Locator,
                    evidence.ContentSha256,
                    evidence.OriginalUtf8ByteCount,
                    evidence.IncludedUtf8ByteCount,
                    evidence.IsTruncated,
                    evidence.ClaimCategoryIds)
                && EvidenceSubjectBelongsToBlock(evidence.Subject, proposal.PatchBlock),
                CampaignStateValidationCode.InvalidCorrelation);
            previousId = evidence.EvidenceReferenceId;
        }

        Require(proposal.PatchBlock.ProvenanceRefs.Length <= CampaignStateContract.MaximumEvidenceReferencesPerBlock
            && proposal.PatchBlock.ProvenanceRefs.SequenceEqual(
                proposal.Evidence.Select(evidence => evidence.EvidenceReferenceId),
                StringComparer.Ordinal),
            CampaignStateValidationCode.InvalidReference);
        RequireSha(proposal.StyleProfileCommitmentSha256);
        Require(IsOpaqueId(proposal.ToolPolicyId, 512), CampaignStateValidationCode.InvalidVocabulary);
        RequireSha(proposal.ProposalCommitmentSha256);
        Require(string.Equals(
            proposal.ProposalCommitmentSha256,
            CreateProposalCommitment(
                state,
                proposal.HistoricalScribeRequestSha256,
                proposal.HistoricalAttemptId,
                proposal.ProviderConfigurationId,
                proposal.ModelConfigurationId,
                proposal.ScribeProtocolId,
                proposal.PatchBlock,
                proposal.Evidence,
                proposal.StyleProfileCommitmentSha256,
                proposal.ToolPolicyId),
            StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
    }

    private static bool EvidenceSubjectBelongsToBlock(
        EvidenceSubject subject,
        DocumentationPatchBlockRequest block) =>
        subject.ParentSymbolRef == block.SymbolRef
        && (subject is TargetEvidenceSubject
            || subject is ComponentEvidenceSubject component
                && block.ApplicableComponents.Any(candidate =>
                    candidate.Kind == MapComponentKind(component.ComponentKind)
                    && string.Equals(candidate.Identity, component.Identity, StringComparison.Ordinal)));

    private static DocumentationPatchRequest ParsePatchRequest(
        DocumentationPatchContext context,
        ImmutableArray<CampaignTrustedProposal> proposals)
    {
        var catalog = proposals
            .SelectMany(proposal => proposal.Evidence)
            .Select(evidence => evidence.EvidenceReferenceId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var ordered = OrderPatchProposals(proposals);
        var blocks = ordered.Select(proposal => proposal.PatchBlock).ToImmutableArray();
        var bytes = CampaignStateJson.WritePatchRequest(context, catalog, blocks);
        var parsed = DocumentationPatchValidator.ParseRequest(bytes);
        if (!parsed.IsValid)
        {
            throw Fail(
                CampaignStateValidationCode.InvalidCorrelation,
                "Active campaign projection is not a valid bounded M2 patch request.");
        }

        return parsed.Request!;
    }

    private static ImmutableArray<CampaignTrustedProposal> OrderPatchProposals(
        ImmutableArray<CampaignTrustedProposal> proposals) => proposals
        .OrderBy(
            proposal => proposal.PatchBlock,
            Comparer<DocumentationPatchBlockRequest>.Create(DocumentationPatchValidator.CompareBlocks))
        .ToImmutableArray();

    private static ImmutableArray<CampaignTrustedProposal> ResolvePatchRequestProjection(
        CampaignCheckpointState state,
        DocumentationPatchRequest request)
    {
        RequirePatchContextMatchesState(state, request.Context);
        var active = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var accepted = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var activeMatches = MatchesPatchRequest(request, active);
        var acceptedMatches = MatchesPatchRequest(request, accepted);
        Require(activeMatches || acceptedMatches, CampaignStateValidationCode.InvalidCorrelation);
        return activeMatches ? active : accepted;
    }

    private static void RequirePatchContextMatchesState(
        CampaignCheckpointState state,
        DocumentationPatchContext context) =>
        Require(
            context.TargetProfile == state.Snapshot.TargetProfile
            && string.Equals(
                CreateInputIdentityCommitment(context.InputIdentity),
                state.Snapshot.InputIdentityCommitmentSha256,
                StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);

    private static bool MatchesPatchRequest(
        DocumentationPatchRequest request,
        ImmutableArray<CampaignTrustedProposal> proposals) =>
        !proposals.IsEmpty
        && string.Equals(
            ParsePatchRequest(request.Context, proposals).ArtifactSha256,
            request.ArtifactSha256,
            StringComparison.Ordinal);

    private static void ValidateClosedOutcome(
        CampaignWorkClosedOutcome outcome,
        string workItemKey)
    {
        Require(string.Equals(outcome.BoundWorkItemKey, workItemKey, StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        Require(Enum.IsDefined(outcome.Stage) && Enum.IsDefined(outcome.Code), CampaignStateValidationCode.InvalidVocabulary);
        if (outcome.Stage == CampaignWorkOutcomeStage.Planning)
        {
            Require(outcome.Code == CampaignWorkOutcomeCode.PlanningTerminal
                && outcome.ProviderDisposition is null
                && outcome.ScribeRequestSha256 is null
                && outcome.AttemptId is null
                && outcome.PatchRequestSha256 is null
                && outcome.PatchResultCommitmentSha256 is null
                && outcome.ScribeResultCommitmentSha256 is null,
                CampaignStateValidationCode.InvalidShape);
        }
        else if (outcome.Stage == CampaignWorkOutcomeStage.Scribe)
        {
            Require(outcome.Code is not CampaignWorkOutcomeCode.PlanningTerminal
                    and not CampaignWorkOutcomeCode.PatchRejected
                && (outcome.Code == CampaignWorkOutcomeCode.ProviderFailure
                    ? outcome.ProviderDisposition is not null && Enum.IsDefined(outcome.ProviderDisposition.Value)
                    : outcome.ProviderDisposition is null)
                && IsSha256(outcome.ScribeRequestSha256)
                && outcome.AttemptId is { } attempt
                && DocumentationScribeAttemptId.TryParse(attempt.Value, out _)
                && outcome.PatchRequestSha256 is null
                && outcome.PatchResultCommitmentSha256 is null
                && (outcome.Code == CampaignWorkOutcomeCode.CompletedOverBound
                    ? IsSha256(outcome.ScribeResultCommitmentSha256)
                    : outcome.ScribeResultCommitmentSha256 is null),
                CampaignStateValidationCode.InvalidShape);
        }
        else
        {
            Require(outcome.Stage == CampaignWorkOutcomeStage.Patch
                && outcome.Code == CampaignWorkOutcomeCode.PatchRejected
                && outcome.ProviderDisposition is null
                && outcome.ScribeRequestSha256 is null
                && outcome.AttemptId is null
                && IsSha256(outcome.PatchRequestSha256)
                && IsSha256(outcome.PatchResultCommitmentSha256)
                && outcome.ScribeResultCommitmentSha256 is null,
                CampaignStateValidationCode.InvalidShape);
        }
    }

    private static void ValidateActiveProjectionSet(ImmutableArray<CampaignWorkItemState> workItems)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var locators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in workItems
            .Where(item => item.TrustedProposal is not null)
            .Select(item => item.TrustedProposal!.PatchBlock))
        {
            var symbolKey = block.SymbolRef.CompilationContextRef + "\0" + block.SymbolRef.DocumentationCommentId;
            Require(symbols.Add(symbolKey), CampaignStateValidationCode.InvalidOrder);
            Require(locators.Add(GetPatchLocatorKey(block.Locator)), CampaignStateValidationCode.InvalidOrder);
        }
    }

    private static void ValidateReservation(CampaignCheckpointState state)
    {
        switch (state.ActiveReservation)
        {
            case null:
                return;
            case CampaignProviderReservation provider:
                Require(IsWorkItemKey(provider.WorkItemKey)
                    && IsSha256(provider.ScribeRequestSha256)
                    && DocumentationScribeAttemptId.TryParse(provider.AttemptId.Value, out _)
                    && provider.Exposure is not null
                    && provider.Exposure.ProviderRequests >= 0
                    && provider.Exposure.ProviderRequests <= state.ConfiguredCeilings.ScribeRunLimits.MaximumProviderRequests
                    && provider.Exposure.InputTokens >= 0
                    && provider.Exposure.InputTokens <= state.ConfiguredCeilings.ScribeRunLimits.MaximumInputTokens
                    && provider.Exposure.UncachedInputTokens >= 0
                    && provider.Exposure.UncachedInputTokens <= provider.Exposure.InputTokens
                    && provider.Exposure.UncachedInputTokens <= state.ConfiguredCeilings.ScribeRunLimits.MaximumUncachedInputTokens
                    && provider.Exposure.OutputTokens >= 0
                    && provider.Exposure.OutputTokens <= state.ConfiguredCeilings.ScribeRunLimits.MaximumOutputTokens
                    && provider.Exposure.CostMicrounits >= 0
                    && provider.Exposure.CostMicrounits <= state.ConfiguredCeilings.ScribeRunLimits.MaximumCostMicrounits
                    && provider.Exposure.ElapsedMilliseconds >= 0
                    && provider.Exposure.ElapsedMilliseconds <= state.ConfiguredCeilings.ScribeRunLimits.MaximumElapsedMilliseconds,
                    CampaignStateValidationCode.InvalidBound);
                Require(state.WorkItems.Count(item =>
                    string.Equals(item.WorkItemKey, provider.WorkItemKey, StringComparison.Ordinal)
                    && item.Status == CampaignWorkStatus.Planned) == 1,
                    CampaignStateValidationCode.InvalidCorrelation);
                break;
            case CampaignPatchReservation patch:
                Require(IsSha256(patch.PatchRequestSha256)
                    && patch.ExpectedCheckpointRevision == state.CheckpointRevision
                    && patch.PatchAttemptCount > 0
                    && patch.PatchAttemptCount <= 1_000
                    && patch.ElapsedMilliseconds >= 0
                    && patch.ElapsedMilliseconds <= CampaignStateContract.MaximumObservation,
                    CampaignStateValidationCode.InvalidCorrelation);
                Require(state.WorkItems.Any(item =>
                    item.Status is CampaignWorkStatus.Accepted or CampaignWorkStatus.ProposalComplete),
                    CampaignStateValidationCode.InvalidCorrelation);
                Require(state.CumulativeOutcome is null
                    || !string.Equals(
                        state.CumulativeOutcome.PatchRequestSha256,
                        patch.PatchRequestSha256,
                        StringComparison.Ordinal)
                    || state.CumulativeOutcome.CompletedFromCheckpointRevision != patch.ExpectedCheckpointRevision,
                    CampaignStateValidationCode.InvalidCorrelation);
                break;
            default:
                throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown campaign reservation kind.");
        }
    }

    private static void ValidateCandidate(CampaignCheckpointState state)
    {
        var acceptedProposals = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var accepted = acceptedProposals
            .Select(proposal => proposal.PatchBlock.BlockId)
            .ToImmutableArray();
        if (state.CandidateObservation is null)
        {
            Require(accepted.IsEmpty, CampaignStateValidationCode.InvalidCorrelation);
            return;
        }

        var candidate = state.CandidateObservation;
        Require(!candidate.AcceptedWorkItemKeys.IsDefault
            && candidate.AcceptedWorkItemKeys.SequenceEqual(accepted, StringComparer.Ordinal)
            && !candidate.AcceptedWorkItemKeys.IsEmpty
            && IsSha256(candidate.AcceptedProjectionCommitmentSha256)
            && string.Equals(
                candidate.AcceptedProjectionCommitmentSha256,
                CreateAcceptedProjectionCommitment(acceptedProposals),
                StringComparison.Ordinal)
            && !candidate.ChangedFiles.IsDefault
            && candidate.ChangedFiles.Length <= state.ConfiguredCeilings.CampaignBudget.MaximumChangedFiles
            && candidate.ChangedFiles.Length <= CampaignStateContract.MaximumChangedFiles
            && IsSha256(candidate.PatchRequestSha256)
            && IsSha256(candidate.PatchResultCommitmentSha256),
            CampaignStateValidationCode.InvalidCorrelation);
        string? previousPath = null;
        long blocks = 0;
        long patchBytes = 0;
        foreach (var file in candidate.ChangedFiles)
        {
            Require(file is not null
                && IsCanonicalRepositoryPath(file.Path)
                && (previousPath is null || string.CompareOrdinal(previousPath, file.Path) < 0)
                && IsSha256(file.OriginalFileSha256)
                && IsSha256(file.CandidateFileSha256)
                && !string.Equals(file.OriginalFileSha256, file.CandidateFileSha256, StringComparison.Ordinal)
                && file.ChangedDocumentationBlockCount > 0
                && file.OriginalDocumentationByteCount >= 0
                && file.CandidateDocumentationByteCount > 0
                && file.OriginalDocumentationLineCount >= 0
                && file.CandidateDocumentationLineCount > 0,
                CampaignStateValidationCode.InvalidCorrelation);
            previousPath = file.Path;
            blocks = checked(blocks + file.ChangedDocumentationBlockCount);
            patchBytes = checked(patchBytes + file.CandidateDocumentationByteCount);
        }

        Require(blocks == accepted.Length
            && blocks <= state.ConfiguredCeilings.CampaignBudget.MaximumBlocks
            && patchBytes <= state.ConfiguredCeilings.CampaignBudget.MaximumPatchBytes,
            CampaignStateValidationCode.InvalidBound);
    }

    private static void ValidateCumulativeOutcome(CampaignCheckpointState state)
    {
        if (state.CumulativeOutcome is null)
        {
            return;
        }

        var outcome = state.CumulativeOutcome;
        Require(Enum.IsDefined(outcome.Kind)
            && IsSha256(outcome.PatchRequestSha256)
            && IsSha256(outcome.ProjectionCommitmentSha256)
            && outcome.CompletedFromCheckpointRevision >= 0
            && outcome.CompletedFromCheckpointRevision <= state.CheckpointRevision,
            CampaignStateValidationCode.InvalidCorrelation);
        var requiresResultCommitment = outcome.Kind is
            CampaignCumulativeOutcomeKind.Accepted
            or CampaignCumulativeOutcomeKind.OverBound
            or CampaignCumulativeOutcomeKind.Rejected
            or CampaignCumulativeOutcomeKind.Stale;
        Require(requiresResultCommitment
            ? IsSha256(outcome.PatchResultCommitmentSha256)
                && (outcome.Kind != CampaignCumulativeOutcomeKind.Accepted
                    || state.CandidateObservation is { } candidate
                        && string.Equals(
                            candidate.PatchRequestSha256,
                            outcome.PatchRequestSha256,
                            StringComparison.Ordinal)
                        && string.Equals(
                            candidate.PatchResultCommitmentSha256,
                            outcome.PatchResultCommitmentSha256,
                            StringComparison.Ordinal))
            : outcome.PatchResultCommitmentSha256 is null,
            CampaignStateValidationCode.InvalidCorrelation);
    }

    private static void ValidateKnownCompletedOperations(CampaignCheckpointState state)
    {
        Require(!state.KnownCompletedOperations.IsDefault
            && state.KnownCompletedOperations.Length <= CampaignStateContract.MaximumKnownPatchCompletedOperations,
            CampaignStateValidationCode.InvalidBound);
        var active = state.WorkItems
            .Where(item => item.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var accepted = state.WorkItems
            .Where(item => item.Status == CampaignWorkStatus.Accepted)
            .Select(item => item.TrustedProposal!)
            .ToImmutableArray();
        var activeCommitment = active.IsEmpty ? null : CreateAcceptedProjectionCommitment(active);
        var acceptedCommitment = accepted.IsEmpty ? null : CreateAcceptedProjectionCommitment(accepted);
        string? priorKey = null;
        foreach (var operation in state.KnownCompletedOperations)
        {
            var key = string.Join('\0',
                operation.Kind,
                operation.ProjectionCommitmentSha256,
                operation.RequestCommitmentSha256,
                operation.ResultCommitmentSha256);
            Require(operation.Kind == "patch-over-bound"
                && IsSha256(operation.RequestCommitmentSha256)
                && IsSha256(operation.ProjectionCommitmentSha256)
                && IsSha256(operation.ResultCommitmentSha256)
                && IsSha256(operation.BindingCommitmentSha256)
                && string.Equals(
                    operation.BindingCommitmentSha256,
                    CreateKnownCompletedOperationBinding(
                        operation.Kind,
                        operation.RequestCommitmentSha256,
                        operation.ProjectionCommitmentSha256,
                        operation.ResultCommitmentSha256),
                    StringComparison.Ordinal)
                && (string.Equals(operation.ProjectionCommitmentSha256, activeCommitment, StringComparison.Ordinal)
                    || string.Equals(operation.ProjectionCommitmentSha256, acceptedCommitment, StringComparison.Ordinal))
                && (priorKey is null || string.CompareOrdinal(priorKey, key) < 0),
                CampaignStateValidationCode.InvalidCorrelation);
            priorKey = key;
        }

        if (state.CumulativeOutcome is
            {
                Kind: CampaignCumulativeOutcomeKind.OverBound,
                PatchResultCommitmentSha256: { } result,
                ProjectionCommitmentSha256: { } projection,
            } cumulative)
        {
            Require(state.KnownCompletedOperations.Any(operation =>
                    string.Equals(operation.RequestCommitmentSha256, cumulative.PatchRequestSha256, StringComparison.Ordinal)
                    && string.Equals(operation.ProjectionCommitmentSha256, projection, StringComparison.Ordinal)
                    && string.Equals(operation.ResultCommitmentSha256, result, StringComparison.Ordinal)),
                CampaignStateValidationCode.InvalidCorrelation);
        }

        var hasScribeOverbound = state.WorkItems.Any(item => item.ClosedOutcome is
        {
            Stage: CampaignWorkOutcomeStage.Scribe,
            Code: CampaignWorkOutcomeCode.CompletedOverBound,
        });
        if (!state.KnownCompletedOperations.IsEmpty || hasScribeOverbound)
        {
            Require(state.ActiveReservation is CampaignPatchReservation
                ? state.TerminalOutcome is null
                : state.TerminalOutcome is
                {
                    Kind: CampaignTerminalKind.Exhausted,
                    Reason: CampaignTerminalReason.Budget,
                }, CampaignStateValidationCode.InvalidCorrelation);
        }
    }

    private static void ValidateTerminal(CampaignCheckpointState state)
    {
        if (state.TerminalOutcome is null)
        {
            return;
        }

        var terminal = state.TerminalOutcome;
        Require(Enum.IsDefined(terminal.Kind)
            && Enum.IsDefined(terminal.Reason)
            && terminal.Kind switch
            {
                CampaignTerminalKind.Complete when terminal.Reason == CampaignTerminalReason.NoWork =>
                    state.WorkItems.IsEmpty,
                CampaignTerminalKind.Complete when terminal.Reason == CampaignTerminalReason.AllWorkClosed =>
                    !state.WorkItems.IsEmpty
                    && state.WorkItems.All(item => item.Status is CampaignWorkStatus.Closed or CampaignWorkStatus.Accepted),
                CampaignTerminalKind.Exhausted => terminal.Reason == CampaignTerminalReason.Budget,
                CampaignTerminalKind.Cancelled => terminal.Reason == CampaignTerminalReason.Caller,
                CampaignTerminalKind.Timeout => terminal.Reason == CampaignTerminalReason.Deadline,
                CampaignTerminalKind.Failed => terminal.Reason == CampaignTerminalReason.Host,
                CampaignTerminalKind.Superseded => terminal.Reason == CampaignTerminalReason.NewSnapshot,
                _ => false,
            }
            && state.ActiveReservation is null,
            CampaignStateValidationCode.InvalidShape);
    }

    private static void ValidatePredecessor(CampaignCheckpointState state)
    {
        if (state.Predecessor is null)
        {
            return;
        }

        var predecessor = state.Predecessor;
        Require(predecessor.ProductRevision is not null
            && predecessor.Snapshot is not null
            && IsOpaqueId(predecessor.ProductRevision.Id, 512)
            && IsSha256(predecessor.ProductRevision.ContentSha256)
            && IsSha256(predecessor.CampaignConfigurationCommitmentSha256)
            && predecessor.FinalCheckpointRevision is >= 0 and <= CampaignStateContract.MaximumObservation
            && IsSha256(predecessor.FinalCheckpointSha256)
            && Enum.IsDefined(predecessor.TerminalKind)
            && predecessor.Candidate is not null
            && predecessor.Candidate.AcceptedCount is >= 0 and <= CampaignStateContract.MaximumActivePatchBlocks
            && predecessor.Candidate.DistinctFileCount is >= 0 and <= CampaignStateContract.MaximumChangedFiles
            && predecessor.Candidate.DistinctFileCount <= predecessor.Candidate.AcceptedCount
            && predecessor.Candidate.OriginalDocumentationByteCount is >= 0 and <= CampaignStateContract.MaximumObservation
            && predecessor.Candidate.CandidateDocumentationByteCount is >= 0 and <= CampaignStateContract.MaximumObservation
            && predecessor.Candidate.OriginalDocumentationLineCount is >= 0 and <= CampaignStateContract.MaximumObservation
            && predecessor.Candidate.CandidateDocumentationLineCount is >= 0 and <= CampaignStateContract.MaximumObservation
            && !string.Equals(
                predecessor.Snapshot.OpaqueSnapshotBinding,
                state.Snapshot.OpaqueSnapshotBinding,
                StringComparison.Ordinal)
            && !string.Equals(
                predecessor.Snapshot.ExecutionCommitmentSha256,
                state.Snapshot.ExecutionCommitmentSha256,
                StringComparison.Ordinal),
            CampaignStateValidationCode.InvalidCorrelation);
        ValidateSnapshot(predecessor.Snapshot);
        Require(predecessor.Candidate.AcceptedCount == 0
            ? predecessor.Candidate.DistinctFileCount == 0
                && predecessor.Candidate.OriginalDocumentationByteCount == 0
                && predecessor.Candidate.CandidateDocumentationByteCount == 0
                && predecessor.Candidate.OriginalDocumentationLineCount == 0
                && predecessor.Candidate.CandidateDocumentationLineCount == 0
                && predecessor.Candidate.PatchRequestSha256 is null
                && predecessor.Candidate.PatchResultCommitmentSha256 is null
            : predecessor.Candidate.DistinctFileCount > 0
                && predecessor.Candidate.CandidateDocumentationByteCount > 0
                && predecessor.Candidate.CandidateDocumentationLineCount > 0
                && IsSha256(predecessor.Candidate.PatchRequestSha256)
                && IsSha256(predecessor.Candidate.PatchResultCommitmentSha256),
            CampaignStateValidationCode.InvalidShape);
        if (predecessor.Reservation is not null)
        {
            Require(predecessor.Reservation.Kind is "provider" or "patch"
                && IsSha256(predecessor.Reservation.CorrelationSha256)
                && predecessor.Reservation.ConservativeCharge is >= 0 and <= CampaignStateContract.MaximumObservation,
                CampaignStateValidationCode.InvalidShape);
        }
        Require(!predecessor.CompletedOperations.IsDefault
            && predecessor.CompletedOperations.Length <= CampaignStateContract.MaximumKnownCompletedOperations,
            CampaignStateValidationCode.InvalidBound);
        string? priorCompleted = null;
        foreach (var completed in predecessor.CompletedOperations)
        {
            var key = string.Join('\0', completed.Kind, completed.ProjectionCommitmentSha256, completed.ResultCommitmentSha256);
            Require(
                completed.Kind is "scribe-over-bound" or "patch-over-bound"
                && IsSha256(completed.ProjectionCommitmentSha256)
                && IsSha256(completed.ResultCommitmentSha256)
                && (priorCompleted is null || string.CompareOrdinal(priorCompleted, key) < 0),
                CampaignStateValidationCode.InvalidShape);
            priorCompleted = key;
        }
    }

    private static CampaignEvidenceProjection ProjectEvidence(
        DocumentationScribeEvidenceReference evidence) => new(
            evidence.EvidenceReferenceId,
            evidence.Subject,
            evidence.Kind,
            evidence.Relation,
            evidence.Authority,
            evidence.Locator,
            evidence.ContentSha256,
            evidence.OriginalUtf8ByteCount,
            evidence.IncludedUtf8ByteCount,
            evidence.IsTruncated,
            evidence.ClaimCategoryIds);

    private static DocumentationPatchSourceLocator CreatePatchLocator(
        CampaignPlanningSourceAuthority source) => source switch
        {
            CampaignPlanningRepositorySourceAuthority repository =>
                new DocumentationPatchRepositoryLocator(
                    repository.Path,
                    repository.ContentSha256,
                    repository.Encoding,
                    repository.RequestedDeclarationSpan),
            CampaignPlanningGeneratedSourceAuthority generated
                when generated.Kind == DocumentationPatchSourceKind.SourceGenerator =>
                new DocumentationPatchSourceGeneratorLocator(
                    generated.ProducerId,
                    generated.OutputId,
                    generated.ContentSha256,
                    generated.RequestedDeclarationSpan),
            CampaignPlanningGeneratedSourceAuthority generated
                when generated.Kind == DocumentationPatchSourceKind.ToolGenerated =>
                new DocumentationPatchToolGeneratedLocator(
                    generated.ProducerId,
                    generated.OutputId,
                    generated.ContentSha256,
                    generated.RequestedDeclarationSpan),
            _ => throw Fail(
                CampaignStateValidationCode.InvalidVocabulary,
                "Work source kind cannot be projected to M2."),
        };

    private static EvidenceLocator CreateScribeSourceLocator(
        CampaignPlanningSourceAuthority source) => source switch
        {
            CampaignPlanningRepositorySourceAuthority repository =>
                new RepositoryEvidenceLocator(repository.Path, repository.RequestedDeclarationSpan),
            CampaignPlanningGeneratedSourceAuthority generated =>
                new GeneratedOutputEvidenceLocator(
                    generated.Kind switch
                    {
                        DocumentationPatchSourceKind.SourceGenerator => GeneratedOutputKind.SourceGenerator,
                        DocumentationPatchSourceKind.ToolGenerated => GeneratedOutputKind.ToolGenerated,
                        _ => throw Fail(
                            CampaignStateValidationCode.InvalidVocabulary,
                            "Unknown generated planning source kind."),
                    },
                    generated.ProducerId,
                    generated.OutputId,
                    generated.ContentSha256,
                    generated.RequestedDeclarationSpan),
            _ => throw Fail(
                CampaignStateValidationCode.InvalidVocabulary,
                "Work source kind cannot be projected to M3."),
        };

    private static DocumentationPatchComponentKind MapComponentKind(ComponentKind kind) => kind switch
    {
        ComponentKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
        ComponentKind.Parameter => DocumentationPatchComponentKind.Parameter,
        ComponentKind.Return => DocumentationPatchComponentKind.Return,
        ComponentKind.Value => DocumentationPatchComponentKind.Value,
        _ => throw Fail(
            CampaignStateValidationCode.InvalidVocabulary,
            "Component kind cannot be projected to M2."),
    };

    private static string CreateStyleProfileCommitment(DocumentationScribeStyleProfile profile)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-style-profile/v1");
        writer.Add("id", profile.StyleProfileId);
        writer.Add("language", profile.OutputLanguageId);
        AddTextPolicy(writer, "summary", profile.Summary);
        AddTextPolicy(writer, "remarks", profile.Remarks);
        AddTextPolicy(writer, "exceptions", profile.Exceptions);
        writer.Add("components.count", profile.ComponentPolicies.Length);
        foreach (var component in profile.ComponentPolicies)
        {
            writer.Add("component.identity", component.ComponentIdentity);
            writer.Add("component.disposition", DocumentationScribeVocabulary.GetId(component.Disposition));
            writer.Add("component.maximum-scalars", component.MaximumScalars);
        }

        writer.Add("inherit-doc", DocumentationScribeVocabulary.GetId(profile.InheritDocDisposition));
        writer.Add("allowed.count", profile.AllowedLiterals.Length);
        foreach (var value in profile.AllowedLiterals)
        {
            writer.Add("allowed", value);
        }

        writer.Add("forbidden.count", profile.ForbiddenLiterals.Length);
        foreach (var value in profile.ForbiddenLiterals)
        {
            writer.Add("forbidden", value);
        }

        writer.Add("claims.count", profile.ClaimPolicies.Length);
        foreach (var claim in profile.ClaimPolicies)
        {
            writer.Add("claim.id", claim.ClaimCategoryId);
            writer.Add("claim.complete", claim.CompleteEvidenceRequired);
            writer.Add("claim.authorities.count", claim.AllowedAuthorities.Length);
            foreach (var authority in claim.AllowedAuthorities)
            {
                writer.Add("claim.authority", DocumentationScribeVocabulary.GetId(authority));
            }
        }

        writer.Add("maximum-content-units", profile.MaximumContentUnits);
        writer.Add("maximum-evidence-refs", profile.MaximumEvidenceRefsPerUnit);
        return writer.Complete();
    }

    private static string CreateAcceptedProjectionCommitment(
        ImmutableArray<CampaignTrustedProposal> proposals)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-accepted-projection/v1");
        writer.Add("proposal.count", proposals.Length);
        foreach (var proposal in proposals)
        {
            writer.Add("work", proposal.PatchBlock.BlockId);
            writer.Add("proposal", proposal.ProposalCommitmentSha256);
            writer.Add("block", CreatePatchBlockCommitment(proposal.PatchBlock));
        }

        return writer.Complete();
    }

    private static string CreatePatchBlockCommitment(DocumentationPatchBlockRequest block)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-patch-block/v1");
        writer.Add("id", block.BlockId);
        AddSymbol(writer, "symbol", block.SymbolRef);
        AddPatchLocator(writer, "locator", block.Locator);
        writer.Add("edit", PatchEditKindId(block.EditKind));
        writer.Add("component.count", block.ApplicableComponents.Length);
        foreach (var component in block.ApplicableComponents)
        {
            writer.Add("component.kind", PatchComponentKindId(component.Kind));
            writer.Add("component.identity", component.Identity);
            writer.AddOptional("component.name", component.Name);
        }

        writer.Add("content", CreatePatchContentCommitment(block.Content));
        writer.Add("provenance.count", block.ProvenanceRefs.Length);
        foreach (var provenance in block.ProvenanceRefs)
        {
            writer.Add("provenance", provenance);
        }

        return writer.Complete();
    }

    private static string CreateProposalCommitment(
        CampaignCheckpointState state,
        DocumentationScribeRequest request,
        DocumentationScribeRunResult result,
        DocumentationPatchBlockRequest block,
        ImmutableArray<CampaignEvidenceProjection> evidence,
        string styleCommitment)
        => CreateProposalCommitment(
            state,
            request.ArtifactSha256,
            result.AttemptId,
            result.RunEnvelope.ProviderConfigurationId,
            result.RunEnvelope.ModelConfigurationId,
            result.RunEnvelope.ScribeProtocolId,
            block,
            evidence,
            styleCommitment,
            request.ToolPolicyId);

    private static string CreateProposalCommitment(
        CampaignCheckpointState state,
        string historicalScribeRequestSha256,
        DocumentationScribeAttemptId historicalAttemptId,
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId,
        DocumentationPatchBlockRequest block,
        ImmutableArray<CampaignEvidenceProjection> evidence,
        string styleCommitment,
        string toolPolicyId)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-trusted-proposal/v1");
        writer.Add("lineage", state.CampaignLineage);
        writer.Add("snapshot", state.Snapshot.OpaqueSnapshotBinding);
        writer.Add("execution", state.Snapshot.ExecutionCommitmentSha256);
        writer.Add("input-identity", state.Snapshot.InputIdentityCommitmentSha256);
        writer.Add("product", state.ProductRevision.ContentSha256);
        writer.Add("configuration", state.ConfiguredCeilings.CampaignConfigurationCommitmentSha256);
        writer.Add("request", historicalScribeRequestSha256);
        writer.Add("attempt", historicalAttemptId.Value);
        writer.Add("provider-configuration", providerConfigurationId);
        writer.Add("model-configuration", modelConfigurationId);
        writer.Add("scribe-protocol", scribeProtocolId);
        AddScribeLimits(writer, state.ConfiguredCeilings.ScribeRunLimits);
        writer.Add("block", block.BlockId);
        AddSymbol(writer, "symbol", block.SymbolRef);
        AddPatchLocator(writer, "locator", block.Locator);
        writer.Add("edit", PatchEditKindId(block.EditKind));
        writer.Add("component.count", block.ApplicableComponents.Length);
        foreach (var component in block.ApplicableComponents)
        {
            writer.Add("component.kind", PatchComponentKindId(component.Kind));
            writer.Add("component.identity", component.Identity);
            writer.AddOptional("component.name", component.Name);
        }

        writer.Add("content", CreatePatchContentCommitment(block.Content));
        writer.Add("evidence.count", evidence.Length);
        foreach (var item in evidence)
        {
            writer.Add("evidence.id", item.EvidenceReferenceId);
            AddEvidenceSubject(writer, item.Subject);
            writer.Add("evidence.kind", EvidenceVocabulary.GetId(item.Kind));
            writer.Add("evidence.relation", EvidenceVocabulary.GetId(item.Relation));
            writer.Add("evidence.authority", DocumentationScribeVocabulary.GetId(item.Authority));
            writer.Add("evidence.sha", item.ContentSha256);
            writer.Add("evidence.locator", GetEvidenceLocatorKey(item.Locator));
            writer.Add("evidence.original-bytes", item.OriginalUtf8ByteCount);
            writer.Add("evidence.included-bytes", item.IncludedUtf8ByteCount);
            writer.Add("evidence.truncated", item.IsTruncated);
            writer.Add("evidence.claim.count", item.ClaimCategoryIds.Length);
            foreach (var claim in item.ClaimCategoryIds)
            {
                writer.Add("evidence.claim", claim);
            }
        }

        writer.Add("style", styleCommitment);
        writer.Add("tool", toolPolicyId);
        return writer.Complete();
    }

    private static void AddScribeLimits(
        CampaignPlanningCommitmentWriter writer,
        CampaignStateScribeLimits limits)
    {
        writer.Add("limits.context-references", limits.MaximumContextReferences);
        writer.Add("limits.context-bytes", limits.MaximumContextUtf8Bytes);
        writer.Add("limits.evidence-references", limits.MaximumEvidenceReferences);
        writer.Add("limits.evidence-bytes", limits.MaximumEvidenceUtf8Bytes);
        writer.Add("limits.provider-requests", limits.MaximumProviderRequests);
        writer.Add("limits.tool-rounds", limits.MaximumToolRounds);
        writer.Add("limits.tool-calls", limits.MaximumToolCalls);
        writer.Add("limits.attempts", limits.MaximumAttempts);
        writer.Add("limits.input-tokens", limits.MaximumInputTokens);
        writer.Add("limits.uncached-input-tokens", limits.MaximumUncachedInputTokens);
        writer.Add("limits.output-tokens", limits.MaximumOutputTokens);
        writer.Add("limits.cost", limits.MaximumCostMicrounits);
        writer.Add("limits.elapsed", limits.MaximumElapsedMilliseconds);
    }

    private static void AddEvidenceSubject(
        CampaignPlanningCommitmentWriter writer,
        EvidenceSubject subject)
    {
        writer.Add("evidence.subject.kind", subject is TargetEvidenceSubject ? "target" : "component");
        AddSymbol(writer, "evidence.subject.parent", subject.ParentSymbolRef);
        if (subject is ComponentEvidenceSubject component)
        {
            writer.Add("evidence.subject.component-kind", ClassificationVocabulary.GetId(component.ComponentKind));
            writer.Add("evidence.subject.identity", component.Identity);
        }
    }

    private static string CreatePatchContentCommitment(DocumentationPatchContent content)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-patch-content/v1");
        switch (content)
        {
            case DocumentationPatchInheritDocContent:
                writer.Add("kind", "inheritDoc");
                break;
            case DocumentationPatchStructuredContent structured:
                writer.Add("kind", "structured");
                AddLines(writer, "summary", structured.SummaryLines);
                writer.Add("type-parameters.count", structured.TypeParameters.Length);
                foreach (var item in structured.TypeParameters)
                {
                    writer.Add("type-parameter.identity", item.ComponentIdentity);
                    writer.Add("type-parameter.name", item.Name);
                    AddLines(writer, "type-parameter.lines", item.Lines);
                }

                writer.Add("parameters.count", structured.Parameters.Length);
                foreach (var item in structured.Parameters)
                {
                    writer.Add("parameter.identity", item.ComponentIdentity);
                    writer.Add("parameter.name", item.Name);
                    AddLines(writer, "parameter.lines", item.Lines);
                }

                AddComponentContent(writer, "return", structured.Return);
                AddComponentContent(writer, "value", structured.Value);
                writer.Add("exceptions.count", structured.Exceptions.Length);
                foreach (var item in structured.Exceptions)
                {
                    writer.Add("exception.type", item.TypeDocumentationId);
                    AddLines(writer, "exception.lines", item.Lines);
                }

                writer.Add("remarks.present", structured.RemarksLines is not null);
                if (structured.RemarksLines is { } remarks)
                {
                    AddLines(writer, "remarks", remarks);
                }

                break;
            default:
                throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch content kind.");
        }

        return writer.Complete();
    }

    private static void AddTextPolicy(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        DocumentationScribeTextPolicy policy)
    {
        writer.Add(prefix + ".disposition", DocumentationScribeVocabulary.GetId(policy.Disposition));
        writer.Add(prefix + ".maximum-scalars", policy.MaximumScalars);
    }

    private static void AddContentAuthority(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        CampaignPlanningContentAuthority authority)
    {
        writer.Add(prefix + ".family", CampaignPlanningContentAuthority.GetContentFamilyId(authority.Family));
        writer.Add(prefix + ".id", authority.Id);
        writer.Add(prefix + ".sha", authority.ContentSha256);
    }

    private static void AddBudget(
        CampaignPlanningCommitmentWriter writer,
        CampaignStateCampaignBudget budget)
    {
        writer.Add("budget.blocks", budget.MaximumBlocks);
        writer.Add("budget.files", budget.MaximumChangedFiles);
        writer.Add("budget.patch-bytes", budget.MaximumPatchBytes);
        writer.Add("budget.provider-requests", budget.MaximumProviderRequests);
        writer.Add("budget.attempts-per-target", budget.MaximumAttemptsPerTarget);
        writer.Add("budget.input-tokens", budget.MaximumInputTokens);
        writer.Add("budget.uncached-input-tokens", budget.MaximumUncachedInputTokens);
        writer.Add("budget.output-tokens", budget.MaximumOutputTokens);
        writer.Add("budget.cost", budget.MaximumCostMicrounits);
        writer.Add("budget.elapsed", budget.MaximumElapsedMilliseconds);
        writer.Add("budget.candidates-per-block", budget.MaximumCandidatesPerBlock);
    }

    private static void AddLimits(
        CampaignPlanningCommitmentWriter writer,
        CampaignStateScribeLimits limits)
    {
        writer.Add("limits.context-refs", limits.MaximumContextReferences);
        writer.Add("limits.context-bytes", limits.MaximumContextUtf8Bytes);
        writer.Add("limits.evidence-refs", limits.MaximumEvidenceReferences);
        writer.Add("limits.evidence-bytes", limits.MaximumEvidenceUtf8Bytes);
        writer.Add("limits.provider-requests", limits.MaximumProviderRequests);
        writer.Add("limits.tool-rounds", limits.MaximumToolRounds);
        writer.Add("limits.tool-calls", limits.MaximumToolCalls);
        writer.Add("limits.attempts", limits.MaximumAttempts);
        writer.Add("limits.input-tokens", limits.MaximumInputTokens);
        writer.Add("limits.uncached-input-tokens", limits.MaximumUncachedInputTokens);
        writer.Add("limits.output-tokens", limits.MaximumOutputTokens);
        writer.Add("limits.cost", limits.MaximumCostMicrounits);
        writer.Add("limits.elapsed", limits.MaximumElapsedMilliseconds);
    }

    private static void AddSymbol(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        SymbolRef symbol)
    {
        writer.Add(prefix + ".context", symbol.CompilationContextRef);
        writer.Add(prefix + ".documentation-id", symbol.DocumentationCommentId);
    }

    private static void AddPatchLocator(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        DocumentationPatchSourceLocator locator)
    {
        writer.Add(prefix + ".kind", PatchSourceKindId(locator.Kind));
        writer.Add(prefix + ".span.start", locator.DeclarationSpan.Start);
        writer.Add(prefix + ".span.end", locator.DeclarationSpan.End);
        switch (locator)
        {
            case DocumentationPatchRepositoryLocator repository:
                writer.Add(prefix + ".path", repository.Path);
                writer.Add(prefix + ".sha", repository.OriginalFileSha256);
                writer.Add(prefix + ".encoding", PatchEncodingId(repository.Encoding));
                break;
            case DocumentationPatchGeneratedLocator generated:
                writer.Add(prefix + ".producer", generated.ProducerId);
                writer.Add(prefix + ".output", generated.OutputId);
                writer.Add(prefix + ".sha", generated.SourceSha256);
                break;
            default:
                throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch locator kind.");
        }
    }

    private static string GetPatchLocatorKey(DocumentationPatchSourceLocator locator)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-patch-locator/v1");
        AddPatchLocator(writer, "locator", locator);
        return writer.Complete();
    }

    private static string GetEvidenceLocatorKey(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository =>
            "repository\0" + repository.Path + "\0" + FormatSpan(repository.Span),
        MetadataEvidenceLocator metadata =>
            "metadata\0" + metadata.AssemblyIdentity + "\0" + metadata.DocumentationCommentId,
        GeneratedOutputEvidenceLocator generated =>
            "generated\0" + GeneratedOutputKindId(generated.ProducerKind) + "\0" + generated.ProducerId + "\0"
            + generated.OutputId + "\0" + generated.SourceSha256 + "\0" + FormatSpan(generated.Span),
        SyntheticEvidenceLocator synthetic => "synthetic\0" + synthetic.FixtureId,
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown evidence locator kind."),
    };

    private static string FormatSpan(Utf16Span? span) => span is null
        ? "null"
        : span.Value.Start + ":" + span.Value.End;

    private static string PatchOutcomeId(DocumentationPatchOutcome value) => value switch
    {
        DocumentationPatchOutcome.Accepted => "accepted",
        DocumentationPatchOutcome.Rejected => "rejected",
        DocumentationPatchOutcome.Stale => "stale",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch outcome."),
    };

    private static string GeneratedOutputKindId(GeneratedOutputKind value) => value switch
    {
        GeneratedOutputKind.SourceGenerator => "source-generator",
        GeneratedOutputKind.ToolGenerated => "tool-generated",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown generated output kind."),
    };

    private static string PatchTargetStatusId(DocumentationPatchTargetStatus value) => value switch
    {
        DocumentationPatchTargetStatus.Valid => "valid",
        DocumentationPatchTargetStatus.Invalid => "invalid",
        DocumentationPatchTargetStatus.Stale => "stale",
        DocumentationPatchTargetStatus.NotEvaluated => "not-evaluated",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch target status."),
    };

    private static string PatchInvariantStatusId(DocumentationPatchInvariantStatus value) => value switch
    {
        DocumentationPatchInvariantStatus.Passed => "passed",
        DocumentationPatchInvariantStatus.Failed => "failed",
        DocumentationPatchInvariantStatus.NotRun => "not-run",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch invariant status."),
    };

    private static string PatchDiagnosticSeverityId(DocumentationPatchDiagnosticSeverity value) => value switch
    {
        DocumentationPatchDiagnosticSeverity.Error => "error",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch diagnostic severity."),
    };

    private static string PatchEditKindId(DocumentationPatchEditKind value) => value switch
    {
        DocumentationPatchEditKind.Insert => "insert",
        DocumentationPatchEditKind.Replace => "replace",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch edit kind."),
    };

    private static string PatchComponentKindId(DocumentationPatchComponentKind value) => value switch
    {
        DocumentationPatchComponentKind.TypeParameter => "type-parameter",
        DocumentationPatchComponentKind.Parameter => "parameter",
        DocumentationPatchComponentKind.Return => "return",
        DocumentationPatchComponentKind.Value => "value",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch component kind."),
    };

    private static string PatchSourceKindId(DocumentationPatchSourceKind value) => value switch
    {
        DocumentationPatchSourceKind.Repository => "repository",
        DocumentationPatchSourceKind.SourceGenerator => "source-generator",
        DocumentationPatchSourceKind.ToolGenerated => "tool-generated",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch source kind."),
    };

    private static string PatchEncodingId(DocumentationPatchRepositoryEncoding value) => value switch
    {
        DocumentationPatchRepositoryEncoding.Utf8 => "utf-8",
        DocumentationPatchRepositoryEncoding.Utf8Bom => "utf-8-bom",
        DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "utf-16le-bom",
        DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "utf-16be-bom",
        _ => throw Fail(CampaignStateValidationCode.InvalidVocabulary, "Unknown patch encoding."),
    };

    private static void AddLines(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        ImmutableArray<string> lines)
    {
        writer.Add(prefix + ".count", lines.Length);
        foreach (var line in lines)
        {
            writer.Add(prefix + ".line", line);
        }
    }

    private static void AddComponentContent(
        CampaignPlanningCommitmentWriter writer,
        string prefix,
        DocumentationPatchComponentContent? content)
    {
        writer.Add(prefix + ".present", content is not null);
        if (content is not null)
        {
            writer.Add(prefix + ".identity", content.ComponentIdentity);
            AddLines(writer, prefix + ".lines", content.Lines);
        }
    }

    private static string CreateInputIdentityCommitment(string? inputIdentity)
    {
        Require(
            DocumentationScribeValidation.IsStableRepositoryRelativePath(inputIdentity)
            && (inputIdentity!.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || inputIdentity.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || inputIdentity.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)),
            CampaignStateValidationCode.InvalidVocabulary);
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-input-identity/v1");
        writer.Add("input-identity", inputIdentity);
        return writer.Complete();
    }

    private static bool IsWorkItemKey(string? value) =>
        value is { Length: 78 }
        && value.StartsWith("campaign-work.", StringComparison.Ordinal)
        && IsSha256(value[14..]);

    private static void RequireSha(string? value) =>
        Require(IsSha256(value), CampaignStateValidationCode.InvalidVocabulary);

    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        CampaignStateValidationCode code)
    {
        if (!condition)
        {
            throw Fail(code, "Campaign checkpoint violates a closed contract invariant.");
        }
    }
}
