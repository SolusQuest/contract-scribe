using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class CampaignPlanner
{
    private const int MaximumOwners = 4_096;
    private const int MaximumTargets = 16_384;
    private const int MaximumComponents = 65_536;
    private const int MaximumUnresolved = 65_536;
    private const int MaximumRelations = 65_536;
    private const int MaximumOwnerSymbols = 65_536;
    private const int MaximumViolations = 131_072;
    private const int MaximumAuditRows = MaximumTargets + MaximumComponents + MaximumUnresolved;
    private const int MaximumIdentifierScalars = 512;
    private const long MaximumByteBudget = 1_099_511_627_776;
    private const long MaximumTokenBudget = 1_000_000_000_000;
    private const long MaximumCostBudget = 1_000_000_000_000_000;
    private const long MaximumElapsedBudget = 2_678_400_000;
    private const int MaximumScribeTokenBudget = 1_000_000_000;
    private const int MaximumScribeElapsedBudget = 2_000_000_000;
    private const int MaximumRequestBudget = 1_000_000;
    private const int MaximumAttemptBudget = 1_000;
    private const int MaximumAuditDepth = 128;
    private const int MaximumAuditTokens = 2_000_000;
    private const long MaximumAuditCanonicalBytes = 33_554_432;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CampaignWorkPlan Plan(CampaignPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRoot(input);
        ValidateRelations(input.Classifications);

        ValidateAggregateEvidenceBounds(input.EvidenceAuthority);
        ValidateAggregateAuditBounds(input.AuditDocument.Root);
        var auditBytes = AuditJson.Write(input.AuditDocument);
        Require(auditBytes.Length <= MaximumAuditCanonicalBytes,
            CampaignPlanningValidationCode.InvalidBound,
            "Canonical Audit authority exceeds the finite planning byte bound.");
        var auditSha256 = Sha256(auditBytes);
        var auditRows = ReadAuditRows(input.AuditDocument);
        var observationAuthority = ValidateObservations(
            input.Classifications,
            input.Observations);
        var observations = observationAuthority.Observations;
        var sourceSession = CampaignPlanningSourceSessionIndex.Build(observations.Values);
        var applicableComponentsByTarget = BuildApplicableComponentIndex(input.Classifications);
        var evidenceAuthority = ValidateEvidenceAuthority(
            observationAuthority,
            input.EvidenceAuthority);
        ValidateAuditAuthority(input.Classifications, observations, evidenceAuthority, auditRows);
        var violationRows = auditRows.Values
            .Where(row => row.Outcome == AuditOutcome.Violation)
            .OrderBy(row => row.SubjectKey, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(violationRows.Length <= MaximumViolations,
            CampaignPlanningValidationCode.InvalidBound,
            "Violation authority exceeds the finite planning bound.");
        var owners = ValidateOwnerAuthority(
            input.Classifications,
            observations,
            applicableComponentsByTarget,
            input.OwnerAuthority,
            sourceSession,
            violationRows.Select(row => row.ParentSymbolRef).ToHashSet());
        var ownerByTarget = owners
            .SelectMany(owner => owner.Targets.Select(target => (target.Target.SymbolRef, Owner: owner)))
            .ToDictionary(pair => pair.SymbolRef, pair => pair.Owner);
        var causesByOwner = owners.ToDictionary(
            owner => owner.CanonicalOwnerRef,
            _ => ImmutableArray.CreateBuilder<CampaignPlanningViolationCause>(),
            StringComparer.Ordinal);
        foreach (var row in violationRows)
        {
            Require(ownerByTarget.TryGetValue(row.ParentSymbolRef, out var owner),
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Every violation subject must resolve to exactly one owner authority row.");
            causesByOwner[owner!.CanonicalOwnerRef].Add(new CampaignPlanningViolationCause(
                row.ParentSymbolRef,
                row.ComponentKind,
                row.ComponentIdentity,
                row.Reason,
                row.RowSha256));
        }

        var selectedOwners = new List<PendingWorkItem>();
        foreach (var owner in owners)
        {
            var causes = causesByOwner[owner.CanonicalOwnerRef].ToImmutable();
            if (causes.IsEmpty)
            {
                continue;
            }

            var pending = BuildPendingItem(owner, auditRows, causes);
            selectedOwners.Add(pending);
        }

        var ordered = selectedOwners
            .OrderBy(item => item, PendingWorkItemComparer.Instance)
            .ToImmutableArray();
        var executionCommitment = ComputeExecutionCommitment(
            input,
            auditSha256,
            ordered);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var workItems = ordered.Select(item =>
        {
            var key = ComputeWorkItemKey(executionCommitment, item);
            Require(
                keys.Add(key),
                CampaignPlanningValidationCode.DuplicateWorkItemKey,
                "Campaign work-item keys must be unique within one plan.");
            return new CampaignPlanningWorkItem(
                "campaign-work." + key,
                item.OwnerEquivalenceRef,
                item.Targets,
                item.Causes,
                item.Disposition);
        }).ToImmutableArray();
        var summary = BuildSummary(workItems);
        return new CampaignWorkPlan(
            input.Snapshot.CampaignLineage,
            input.Snapshot.OpaqueSnapshotBinding,
            auditSha256,
            executionCommitment,
            input.Snapshot.TargetProfile,
            workItems,
            summary);
    }

    internal static ImmutableHashSet<SymbolRef> ReadViolationParentSymbols(
        AuditDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ReadAuditRows(document).Values
            .Where(row => row.Outcome == AuditOutcome.Violation)
            .Select(row => row.ParentSymbolRef)
            .ToImmutableHashSet();
    }

    internal static ImmutableHashSet<SymbolRef> ReadExecutableStyleParentSymbols(
        AuditDocument document,
        ImmutableArray<CampaignPlanningOwnerAuthority> owners)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (owners.IsDefault)
        {
            throw new ArgumentException("Owner authority is not initialized.", nameof(owners));
        }

        var violationReasons = ReadAuditRows(document).Values
            .Where(row => row.Outcome == AuditOutcome.Violation)
            .GroupBy(row => row.ParentSymbolRef)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Reason).ToImmutableArray());
        return owners
            .Where(owner => owner is not null && owner.Targets.Length == 1)
            .Select(owner => (Owner: owner, Target: owner.Targets[0]))
            .Where(pair => IsM3Eligible(
                pair.Owner.AmbiguousOwner,
                pair.Owner.Targets,
                pair.Target)
                && violationReasons.TryGetValue(pair.Target.Target.SymbolRef, out var reasons)
                && !reasons.IsEmpty
                && reasons.All(reason => reason == AuditReason.RequiredAbsent))
            .Select(pair => pair.Target.Target.SymbolRef)
            .ToImmutableHashSet();
    }

    private static void ValidateRoot(CampaignPlanningInput input)
    {
        Require(input.Snapshot is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Campaign snapshot authority is required.");
        Require(input.ExecutionPolicy is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Campaign execution policy is required.");
        Require(input.Classifications is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Classification authority is required.");
        Require(input.Observations is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Observation authority is required.");
        Require(!input.EvidenceAuthority.IsDefault, CampaignPlanningValidationCode.InvalidRoot,
            "Observation evidence authority is required.");
        Require(input.AuditDocument is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Audit authority is required.");
        Require(input.OwnerAuthority is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Owner authority is required.");

        var snapshot = input.Snapshot
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Campaign snapshot authority is required.");
        var executionPolicy = input.ExecutionPolicy
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Campaign execution policy is required.");
        var classifications = input.Classifications
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Classification authority is required.");
        var auditDocument = input.AuditDocument
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Audit authority is required.");
        RequireOpaqueIdentifier(snapshot.CampaignLineage, nameof(snapshot.CampaignLineage));
        RequireOpaqueIdentifier(snapshot.OpaqueSnapshotBinding, nameof(snapshot.OpaqueSnapshotBinding));
        RequireSha256(snapshot.RepositoryCommitmentSha256, nameof(snapshot.RepositoryCommitmentSha256));
        RequireSha256(snapshot.InputCommitmentSha256, nameof(snapshot.InputCommitmentSha256));
        RequireSha256(snapshot.PolicyAuthorityCommitmentSha256, nameof(snapshot.PolicyAuthorityCommitmentSha256));
        Require(
            Enum.IsDefined(snapshot.TargetProfile)
            && classifications.TargetProfile == snapshot.TargetProfile
            && auditDocument.TargetProfile == snapshot.TargetProfile,
            CampaignPlanningValidationCode.TargetProfileMismatch,
            "Snapshot, Classification, and Audit target profiles must match.");

        ValidateExecutionPolicy(executionPolicy);
        var ownerAuthority = input.OwnerAuthority
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Owner authority is required.");
        Require(
            !ownerAuthority.Owners.IsDefault
            && ownerAuthority.Owners.Length <= MaximumOwners
            && ownerAuthority.Owners.All(owner => owner is not null)
            && classifications.Targets.Length <= MaximumTargets
            && classifications.Components.Length <= MaximumComponents
            && classifications.Relations.Length <= MaximumRelations
            && classifications.Unresolved.Length <= MaximumUnresolved
            && input.EvidenceAuthority.Length <= MaximumTargets + MaximumComponents
            && auditDocument.Root.GetProperty("results").GetArrayLength() <= MaximumAuditRows,
            CampaignPlanningValidationCode.InvalidBound,
            "Planning authority collections must be initialized and finitely bounded.");
    }

    private static void ValidateRelations(ClassificationSet classifications)
    {
        Require(!classifications.Relations.IsDefault,
            CampaignPlanningValidationCode.InvalidClassificationAuthority,
            "Relation authority must be initialized.");
        var acceptedTargets = classifications.Targets
            .Select(target => target.SymbolRef)
            .ToHashSet();
        RelationObservation? previous = null;
        foreach (var relation in classifications.Relations)
        {
            Require(relation is not null
                    && Enum.IsDefined(relation.RelationKind)
                    && (acceptedTargets.Contains(relation.SourceSymbolRef)
                        || acceptedTargets.Contains(relation.TargetSymbolRef)),
                CampaignPlanningValidationCode.InvalidClassificationAuthority,
                "Every relation must use the closed vocabulary and reference an accepted target.");
            RequireIdentifier(relation!.SourceSymbolRef.CompilationContextRef,
                nameof(relation.SourceSymbolRef.CompilationContextRef));
            RequireText(relation.SourceSymbolRef.DocumentationCommentId,
                nameof(relation.SourceSymbolRef.DocumentationCommentId));
            RequireIdentifier(relation.TargetSymbolRef.CompilationContextRef,
                nameof(relation.TargetSymbolRef.CompilationContextRef));
            RequireText(relation.TargetSymbolRef.DocumentationCommentId,
                nameof(relation.TargetSymbolRef.DocumentationCommentId));
            Require(previous is null || CompareRelation(previous, relation) < 0,
                CampaignPlanningValidationCode.InvalidClassificationAuthority,
                "Relations must be unique and use canonical ordinal order.");
            previous = relation;
        }
    }

    private static int CompareRelation(RelationObservation left, RelationObservation right)
    {
        var comparison = CompareSymbolRef(left.SourceSymbolRef, right.SourceSymbolRef);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            ClassificationVocabulary.GetId(left.RelationKind),
            ClassificationVocabulary.GetId(right.RelationKind));
        return comparison != 0
            ? comparison
            : CompareSymbolRef(left.TargetSymbolRef, right.TargetSymbolRef);
    }

    private static void ValidateAggregateAuditBounds(JsonElement root)
    {
        try
        {
            var tokenCount = 0;
            long estimatedBytes = 0;
            CountAuditValue(root, 1, ref tokenCount, ref estimatedBytes);
            Require(estimatedBytes + 1 <= MaximumAuditCanonicalBytes,
                CampaignPlanningValidationCode.InvalidBound,
                "Audit authority exceeds the finite planning byte bound.");
        }
        catch (CampaignPlanningValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or OverflowException)
        {
            throw Failure(CampaignPlanningValidationCode.InvalidBound,
                "Audit authority exceeds the finite planning byte bound.");
        }
    }

    private static void ValidateAggregateEvidenceBounds(
        ImmutableArray<CampaignPlanningEvidenceAuthority> evidenceAuthority)
    {
        try
        {
            long aggregateBytes = 0;
            long aggregateObservationBytes = 0;
            var aggregateObservationDeclarations = 0;
            var aggregateEvidenceAuthorityRows = 0;
            foreach (var authority in evidenceAuthority)
            {
                Require(authority is not null && authority.Binding is not null,
                    CampaignPlanningValidationCode.InvalidObservationAuthority,
                    "Evidence authority cannot contain null records.");
                aggregateBytes = checked(aggregateBytes
                    + CampaignPlanningEvidenceProjection.EstimateCanonicalBytes(
                        authority!.Binding!.Bundle));
                aggregateObservationBytes = checked(
                    aggregateObservationBytes + authority.ObservationProjectionUtf8Bytes);
                aggregateObservationDeclarations = checked(
                    aggregateObservationDeclarations + authority.ObservationDeclarationCount);
                aggregateEvidenceAuthorityRows = checked(
                    aggregateEvidenceAuthorityRows
                    + (authority.Binding.Authority?.Declarations.Length ?? 0));
                Require(aggregateBytes <= MaximumAuditCanonicalBytes,
                    CampaignPlanningValidationCode.InvalidBound,
                    "Aggregate evidence authority exceeds the finite planning byte bound.");
                Require(
                    authority.ObservationDeclarationCount >= 0
                    && authority.ObservationDeclarationCount
                        <= CampaignPlanningObservationProjection.MaximumDeclarationsPerObservation
                    && authority.ObservationProjectionUtf8Bytes >= 0
                    && authority.ObservationProjectionUtf8Bytes
                        <= CampaignPlanningObservationProjection.MaximumProjectionUtf8Bytes
                    && aggregateObservationDeclarations
                        <= CampaignPlanningObservationProjection.MaximumAggregateDeclarations
                    && aggregateEvidenceAuthorityRows
                        <= CampaignPlanningObservationProjection.MaximumAggregateDeclarations
                    && aggregateObservationBytes
                        <= CampaignPlanningObservationProjection.MaximumAggregateProjectionUtf8Bytes,
                    CampaignPlanningValidationCode.InvalidBound,
                    "Aggregate observation and evidence authority exceeds the finite planning bound.");
            }
        }
        catch (CampaignPlanningValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or OverflowException)
        {
            throw Failure(CampaignPlanningValidationCode.InvalidBound,
                "Aggregate evidence authority exceeds the finite planning byte bound.");
        }
    }

    private static void CountAuditValue(
        JsonElement value,
        int depth,
        ref int tokenCount,
        ref long estimatedBytes)
    {
        Require(depth <= MaximumAuditDepth,
            CampaignPlanningValidationCode.InvalidBound,
            "Audit authority exceeds the finite planning depth bound.");
        tokenCount = checked(tokenCount + 1);
        Require(tokenCount <= MaximumAuditTokens,
            CampaignPlanningValidationCode.InvalidBound,
            "Audit authority exceeds the finite planning token bound.");
        estimatedBytes = checked(estimatedBytes + 16);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    estimatedBytes = checked(estimatedBytes
                        + (long)StrictUtf8.GetByteCount(property.Name) * 6 + 3);
                    CountAuditValue(property.Value, depth + 1, ref tokenCount, ref estimatedBytes);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CountAuditValue(item, depth + 1, ref tokenCount, ref estimatedBytes);
                }
                break;
            case JsonValueKind.String:
                estimatedBytes = checked(estimatedBytes
                    + (long)StrictUtf8.GetByteCount(value.GetString()!) * 6 + 3);
                break;
            case JsonValueKind.Number:
                estimatedBytes = checked(estimatedBytes + value.GetRawText().Length);
                break;
        }

        Require(estimatedBytes <= MaximumAuditCanonicalBytes,
            CampaignPlanningValidationCode.InvalidBound,
            "Audit authority exceeds the finite planning byte bound.");
    }

    private static void ValidateExecutionPolicy(CampaignPlanningExecutionPolicy policy)
    {
        Require(policy.ScribeRunLimits is not null && policy.CampaignBudget is not null,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Scribe and campaign limits are required.");
        foreach (var (authority, family) in new[]
        {
            (policy.ProposalContract, CampaignPlanningContentFamily.ProposalContract),
            (policy.AgentProtocol, CampaignPlanningContentFamily.AgentProtocol),
            (policy.ContextSelectionPolicy, CampaignPlanningContentFamily.ContextSelectionPolicy),
            (policy.ToolPolicyAndRegistry, CampaignPlanningContentFamily.ToolPolicyAndRegistry),
            (policy.ProviderModelRequestProfile, CampaignPlanningContentFamily.ProviderModelRequestProfile),
            (policy.RetryPolicy, CampaignPlanningContentFamily.RetryPolicy),
            (policy.M2ProjectionPolicy, CampaignPlanningContentFamily.M2ProjectionPolicy),
            (policy.ProductContractRevision, CampaignPlanningContentFamily.ProductContractRevision),
        })
        {
            Require(authority is not null, CampaignPlanningValidationCode.InvalidConfiguration,
                "Every correctness-bearing configuration family requires content authority.");
            Require(authority!.Family == family,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Configuration content authority is assigned to the wrong family.");
            RequireOpaqueIdentifier(authority.Id, nameof(authority.Id));
            RequireSha256(authority.ContentSha256, nameof(authority.ContentSha256));
        }

        var limits = policy.ScribeRunLimits!;
        Require(
            limits.MaximumContextReferences >= 0
            && limits.MaximumContextUtf8Bytes >= 0
            && limits.MaximumEvidenceReferences >= 0
            && limits.MaximumEvidenceUtf8Bytes >= 0
            && limits.MaximumProviderRequests is > 0 and <= MaximumRequestBudget
            && limits.MaximumToolRounds >= 0
            && limits.MaximumToolCalls >= 0
            && limits.MaximumAttempts is > 0 and <= MaximumAttemptBudget
            && limits.MaximumInputTokens > 0
            && limits.MaximumInputTokens <= MaximumScribeTokenBudget
            && limits.MaximumUncachedInputTokens >= 0
            && limits.MaximumUncachedInputTokens <= limits.MaximumInputTokens
            && limits.MaximumOutputTokens > 0
            && limits.MaximumOutputTokens <= MaximumScribeTokenBudget
            && limits.MaximumCostMicrounits is >= 0 and <= MaximumCostBudget
            && limits.MaximumElapsedMilliseconds > 0
            && limits.MaximumElapsedMilliseconds <= MaximumScribeElapsedBudget,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Scribe limits must use non-negative bounded values and positive active ceilings.");

        var budget = policy.CampaignBudget!;
        Require(
            budget.MaximumBlocks is > 0 and <= MaximumTargets
            && budget.MaximumChangedFiles is > 0 and <= MaximumTargets
            && budget.MaximumPatchBytes is > 0 and <= MaximumByteBudget
            && budget.MaximumProviderRequests is > 0 and <= MaximumRequestBudget
            && budget.MaximumAttemptsPerTarget is > 0 and <= MaximumAttemptBudget
            && budget.MaximumInputTokens is > 0 and <= MaximumTokenBudget
            && budget.MaximumUncachedInputTokens >= 0
            && budget.MaximumUncachedInputTokens <= budget.MaximumInputTokens
            && budget.MaximumOutputTokens is > 0 and <= MaximumTokenBudget
            && budget.MaximumCostMicrounits is >= 0 and <= MaximumCostBudget
            && budget.MaximumElapsedMilliseconds is > 0 and <= MaximumElapsedBudget
            && budget.MaximumCandidatesPerBlock is > 0 and <= MaximumAttemptBudget,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Campaign budgets must use non-negative bounded values and positive active ceilings.");
        if (budget.CostEnforced)
        {
            RequireOpaqueIdentifier(budget.CostCurrency!, nameof(budget.CostCurrency));
            Require(budget.CostRatePolicy is not null,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Cost enforcement requires currency and rate-policy content authority.");
            Require(budget.CostRatePolicy!.Family == CampaignPlanningContentFamily.CostRatePolicy,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Cost enforcement requires cost-rate family authority.");
            RequireOpaqueIdentifier(budget.CostRatePolicy.Id, nameof(budget.CostRatePolicy.Id));
            RequireSha256(budget.CostRatePolicy.ContentSha256, nameof(budget.CostRatePolicy.ContentSha256));
        }
        else
        {
            Require(budget.CostCurrency is null && budget.CostRatePolicy is null,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Disabled cost enforcement cannot carry contradictory cost authority.");
        }
    }

    private static ValidatedObservationAuthority ValidateObservations(
        ClassificationSet classifications,
        DocumentationObservationSet observations)
    {
        Require(!observations.Observations.IsDefault,
            CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Observation authority must be initialized.");
        var expected = classifications.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .Select(target => TargetKey(target.SymbolRef))
            .Concat(classifications.Components
                .Where(component => component.SupportStatus == SupportStatus.Supported)
                .Select(component => ComponentKey(
                    component.ParentSymbolRef,
                    component.ComponentKind,
                    component.Identity)))
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, DocumentationObservation>(StringComparer.Ordinal);
        var projections = new Dictionary<
            string,
            CampaignPlanningObservationProjectionResult>(StringComparer.Ordinal);
        var aggregateDeclarations = 0;
        long aggregateProjectionBytes = 0;
        foreach (var observation in observations.Observations)
        {
            Require(observation is not null && observation.Subject is not null,
                CampaignPlanningValidationCode.InvalidObservationAuthority,
                "Observation authority contains a null record.");
            var actualObservation = observation!;
            var key = ObservationKey(actualObservation.Subject!);
            var projection = CampaignPlanningObservationProjection.Project(actualObservation);
            aggregateDeclarations = checked(aggregateDeclarations + projection.DeclarationCount);
            aggregateProjectionBytes = checked(
                aggregateProjectionBytes + projection.EstimatedUtf8Bytes);
            Require(expected.Contains(key) && result.TryAdd(key, actualObservation),
                CampaignPlanningValidationCode.InvalidObservationAuthority,
                "Observation authority must cover every and only supported classification once.");
            projections.Add(key, projection);
            Require(
                aggregateDeclarations
                    <= CampaignPlanningObservationProjection.MaximumAggregateDeclarations
                && aggregateProjectionBytes
                    <= CampaignPlanningObservationProjection.MaximumAggregateProjectionUtf8Bytes,
                CampaignPlanningValidationCode.InvalidBound,
                "Aggregate observation authority exceeds the finite planning projection bound.");
        }

        Require(result.Count == expected.Count,
            CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Observation authority must cover every and only supported classification once.");
        return new ValidatedObservationAuthority(result, projections);
    }

    private static Dictionary<string, BoundObservationEvidence> ValidateEvidenceAuthority(
        ValidatedObservationAuthority observationAuthority,
        ImmutableArray<CampaignPlanningEvidenceAuthority> supplied)
    {
        var observations = observationAuthority.Observations;
        Require(supplied.Length == observations.Count,
            CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Evidence authority must cover every and only supported observation once.");
        Require(supplied.All(item => item is not null
                && item.Subject is not null
                && item.Binding is not null),
            CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Evidence authority cannot contain null records.");
        var result = new Dictionary<string, BoundObservationEvidence>(StringComparer.Ordinal);
        foreach (var actualItem in supplied.OrderBy(
                     item => ObservationKey(item!.Subject!),
                     StringComparer.Ordinal))
        {
            var key = ObservationKey(actualItem!.Subject!);
            Require(observations.TryGetValue(key, out var observation)
                    && result.TryAdd(key, actualItem.Binding!)
                    && observationAuthority.Projections.TryGetValue(key, out var projection)
                    && actualItem.ObservationAuthorityCommitmentSha256
                        == projection.CommitmentSha256
                    && actualItem.ObservationDeclarationCount == projection.DeclarationCount
                    && actualItem.ObservationProjectionUtf8Bytes == projection.EstimatedUtf8Bytes
                    && BindingMatchesCurrentObservation(observation!, actualItem.Binding!),
                CampaignPlanningValidationCode.InvalidObservationAuthority,
                "Evidence authority must be reproducible from every accepted current observation exactly.");
        }

        return result;
    }

    private static bool BindingMatchesCurrentObservation(
        DocumentationObservation observation,
        BoundObservationEvidence binding)
    {
        if (binding.ObservationValue != observation.Value)
        {
            return false;
        }

        if (!CampaignPlanningPartialEvidenceValidator.MatchesCurrentObservation(
                observation,
                binding.Bundle))
        {
            return false;
        }

        var declarationBindings = ImmutableArray.CreateBuilder<EvidenceDeclarationBindingInput>();
        if (binding.Authority is { } authority)
        {
            var rows = authority.Declarations.ToDictionary(
                row => row.DeclarationId,
                StringComparer.Ordinal);
            if (rows.Count != observation.Declarations.Length)
            {
                return false;
            }

            foreach (var declaration in observation.Declarations)
            {
                if (!rows.TryGetValue(declaration.DeclarationId, out var row))
                {
                    return false;
                }

                var useDocumentation = RequiresDocumentationEvidence(
                    observation.Subject,
                    declaration);
                declarationBindings.Add(EvidenceBindingInput.Declaration(
                    declaration.DeclarationId,
                    useDocumentation ? null : row.EvidenceId,
                    useDocumentation ? row.EvidenceId : null));
            }
        }

        var rebound = EvidenceObservationBinder.Bind(
            observation,
            binding.Bundle,
            declarationBindings.ToImmutable());
        return rebound.Status == EvidenceRunStatus.Success
            && rebound.Binding is { } current
            && CampaignPlanningEvidenceProjection.Equivalent(binding, current);
    }

    private static bool RequiresDocumentationEvidence(
        DocumentationObservationSubject subject,
        DocumentationDeclarationFact declaration) =>
        declaration.BlockState == DocumentationBlockState.Malformed
        || (subject.ComponentKind is null
            ? declaration.ParentSubstantive
            : declaration.BlockState == DocumentationBlockState.WellFormed
                && declaration.ComponentMatch == DocumentationComponentMatch.Present);

    private static IReadOnlyDictionary<SymbolRef, ImmutableArray<ComponentClassification>>
        BuildApplicableComponentIndex(ClassificationSet classifications)
    {
        var builders = new Dictionary<SymbolRef, ImmutableArray<ComponentClassification>.Builder>();
        var memberships = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in classifications.Components)
        {
            if (component.SupportStatus != SupportStatus.Supported
                || !IsApplicableComponent(component.ComponentKind))
            {
                continue;
            }

            Require(memberships.Add(ComponentKey(
                    component.ParentSymbolRef,
                    component.ComponentKind,
                    component.Identity)),
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Applicable components must have unique canonical parent, kind, and identity membership.");
            if (!builders.TryGetValue(component.ParentSymbolRef, out var builder))
            {
                builder = ImmutableArray.CreateBuilder<ComponentClassification>();
                builders.Add(component.ParentSymbolRef, builder);
            }

            builder.Add(component);
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderBy(component => ComponentRank(component.ComponentKind))
                .ThenBy(component => component.Identity, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static Dictionary<string, AuditRow> ReadAuditRows(AuditDocument document)
    {
        var result = new Dictionary<string, AuditRow>(StringComparer.Ordinal);
        var results = document.Root.GetProperty("results");
        foreach (var row in results.EnumerateArray())
        {
            var classification = row.GetProperty("classification");
            var recordType = classification.GetProperty("recordType").GetString();
            SymbolRef parent;
            ComponentKind? componentKind = null;
            string? componentIdentity = null;
            string key;
            if (recordType == "TargetClassification")
            {
                parent = ReadSymbolRef(classification.GetProperty("symbolRef"));
                key = TargetKey(parent);
            }
            else if (recordType == "ComponentClassification")
            {
                parent = ReadSymbolRef(classification.GetProperty("parentSymbolRef"));
                componentKind = ReadComponentKind(classification.GetProperty("componentKind").GetString());
                componentIdentity = classification.GetProperty("identity").GetString();
                key = ComponentKey(parent, componentKind.Value, componentIdentity!);
            }
            else
            {
                var context = classification.GetProperty("compilationContextRef").GetString()!;
                var locator = classification.GetProperty("candidateLocator").GetRawText();
                key = "u\u001f" + context + "\u001f" + locator;
                parent = default;
            }

            var outcome = ReadAuditOutcome(row.GetProperty("auditOutcome").GetString());
            var reason = ReadAuditReason(row.GetProperty("reasonCode").GetString());
            var canonicalRow = AuditCanonicalJson.Canonicalize(row);
            var evidenceIds = row.GetProperty("evidenceIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToImmutableArray();
            JsonElement? evidenceAuthority = row.TryGetProperty("evidenceAuthority", out var authority)
                ? authority.Clone()
                : null;
            var evidenceBundle = row.GetProperty("evidenceBundle");
            JsonElement? observationSubject = evidenceBundle.TryGetProperty(
                "observationSubject",
                out var subject)
                ? subject.Clone()
                : null;
            Require(result.TryAdd(key, new AuditRow(
                    key,
                    parent,
                    componentKind,
                    componentIdentity,
                    outcome,
                    reason,
                    Sha256(canonicalRow),
                    classification.Clone(),
                    row.GetProperty("documentationObservation").GetString(),
                    evidenceIds,
                    evidenceAuthority,
                    observationSubject,
                    evidenceBundle.Clone())),
                CampaignPlanningValidationCode.InvalidAuditAuthority,
                "Audit authority contains a duplicate classification row.");
        }

        return result;
    }

    private static void ValidateAuditAuthority(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
        IReadOnlyDictionary<string, BoundObservationEvidence> evidenceAuthority,
        IReadOnlyDictionary<string, AuditRow> rows)
    {
        Require(rows.Count == classifications.Targets.Length
                + classifications.Components.Length
                + classifications.Unresolved.Length,
            CampaignPlanningValidationCode.InvalidAuditAuthority,
            "Audit authority must cover every and only accepted classification once.");
        foreach (var target in classifications.Targets)
        {
            var key = TargetKey(target.SymbolRef);
            if (!rows.TryGetValue(key, out var row)
                || !TargetClassificationMatches(target, row.Classification))
            {
                throw Failure(CampaignPlanningValidationCode.InvalidAuditAuthority,
                    "Audit target classification does not match the accepted ClassificationSet.");
            }
            if (target.SupportStatus == SupportStatus.Supported)
            {
                Require(observations.TryGetValue(key, out var observation)
                        && AuditEvidenceMatches(observation.Value, evidenceAuthority[key], row),
                    CampaignPlanningValidationCode.InvalidAuditAuthority,
                    "Audit target observation does not match current observation authority.");
            }
        }

        foreach (var component in classifications.Components)
        {
            var key = ComponentKey(component.ParentSymbolRef, component.ComponentKind, component.Identity);
            if (!rows.TryGetValue(key, out var row)
                || !ComponentClassificationMatches(component, row.Classification))
            {
                throw Failure(CampaignPlanningValidationCode.InvalidAuditAuthority,
                    "Audit component classification does not match the accepted ClassificationSet.");
            }
            if (component.SupportStatus == SupportStatus.Supported)
            {
                Require(observations.TryGetValue(key, out var observation)
                        && AuditEvidenceMatches(observation.Value, evidenceAuthority[key], row),
                    CampaignPlanningValidationCode.InvalidAuditAuthority,
                    "Audit component observation does not match current observation authority.");
            }
        }

        var unresolvedRows = new Dictionary<string, AuditRow>(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(row =>
                     row.Classification.GetProperty("recordType").GetString()
                         == "UnresolvedClassification"))
        {
            Require(unresolvedRows.TryAdd(UnresolvedKey(row.Classification), row),
                CampaignPlanningValidationCode.InvalidAuditAuthority,
                "Audit unresolved classifications cannot duplicate a canonical locator.");
        }

        Require(unresolvedRows.Count == classifications.Unresolved.Length
                && classifications.Unresolved.All(expected =>
                    unresolvedRows.TryGetValue(UnresolvedKey(expected), out var actual)
                    && UnresolvedClassificationMatches(expected, actual.Classification)),
            CampaignPlanningValidationCode.InvalidAuditAuthority,
            "Audit unresolved classifications must exactly match the accepted ClassificationSet.");
    }

    private static bool AuditEvidenceMatches(
        DocumentationObservationValue observation,
        BoundObservationEvidence binding,
        AuditRow row)
    {
        if (row.Reason is AuditReason.PolicyConflict or AuditReason.PolicyUnavailable)
        {
            return row.ObservationId is null
                && row.EvidenceIds.IsEmpty
                && row.EvidenceAuthority is null
                && row.ObservationSubject is null
                && CampaignPlanningEvidenceProjection.MatchesUnavailable(
                    row.EvidenceBundle,
                    EvidenceOmissionReason.NotProvided);
        }

        var expectedObservation = row.Reason is AuditReason.EvidenceIncomplete
            or AuditReason.DocumentationUnavailable
            or AuditReason.DocumentationUnavailableMalformedXml
            ? DocumentationObservationValue.Unavailable
            : observation;
        return ObservationId(expectedObservation) == row.ObservationId
            && EvidenceAuthorityMatches(binding, row)
            && CampaignPlanningEvidenceProjection.Matches(binding.Bundle, row.EvidenceBundle);
    }

    private static bool EvidenceAuthorityMatches(
        BoundObservationEvidence binding,
        AuditRow row)
    {
        if (!binding.EvidenceIds.SequenceEqual(row.EvidenceIds, StringComparer.Ordinal))
        {
            return false;
        }

        if (binding.Authority is null)
        {
            return row.EvidenceAuthority is null && row.ObservationSubject is null;
        }

        if (row.EvidenceAuthority is not { } authority
            || row.ObservationSubject is not { } observationSubject
            || authority.GetProperty("declarationSetId").GetString()
                != binding.Authority.DeclarationSetId
            || authority.GetProperty("completeness").GetString()
                != (binding.Authority.Completeness == EvidenceAuthorityCompleteness.Complete
                    ? "complete"
                    : "positive-only"))
        {
            return false;
        }

        var declarations = authority.GetProperty("declarations").EnumerateArray().ToArray();
        if (declarations.Length != binding.Authority.Declarations.Length)
        {
            return false;
        }

        for (var index = 0; index < declarations.Length; index++)
        {
            var actual = declarations[index];
            var expected = binding.Authority.Declarations[index];
            if (actual.GetProperty("declarationId").GetString() != expected.DeclarationId
                || actual.GetProperty("authorityRole").GetString()
                    != AuditAuthorityRoleId(expected.AuthorityRole)
                || actual.GetProperty("blockState").GetString()
                    != AuditBlockStateId(expected.BlockState)
                || actual.GetProperty("evidenceId").GetString() != expected.EvidenceId)
            {
                return false;
            }

            var actualName = actual.TryGetProperty("componentLocalName", out var name)
                ? name.GetString()
                : null;
            var actualMatch = actual.TryGetProperty("componentMatch", out var match)
                ? match.GetString()
                : null;
            if (actualName != expected.ComponentLocalName
                || actualMatch != (expected.ComponentMatch is null
                    ? null
                    : AuditComponentMatchId(expected.ComponentMatch.Value)))
            {
                return false;
            }
        }

        var subject = binding.Bundle.ObservationSubject;
        return subject is not null
            && observationSubject.GetProperty("observationSubjectRef").GetString()
                == subject.ObservationSubjectRef
            && observationSubject.GetProperty("compilationContextRef").GetString()
                == subject.CompilationContextRef
            && ReadEvidenceSubject(observationSubject.GetProperty("subject")) == subject.Subject
            && observationSubject.GetProperty("authoritativeDeclarationSetDigest").GetString()
                == subject.AuthoritativeDeclarationSetDigest
            && observationSubject.GetProperty("authoritativeDeclarationCount").GetInt32()
                == subject.AuthoritativeDeclarationCount;
    }

    private static ImmutableArray<ValidatedOwnerAuthority> ValidateOwnerAuthority(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
        IReadOnlyDictionary<SymbolRef, ImmutableArray<ComponentClassification>> applicableComponentsByTarget,
        CampaignPlanningOwnerAuthoritySet authoritySet,
        CampaignPlanningSourceSessionIndex sourceSession,
        IReadOnlySet<SymbolRef> violationParents)
    {
        var supportedTargets = classifications.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .ToDictionary(target => target.SymbolRef);
        var targetMembership = new Dictionary<SymbolRef, CampaignPlanningOwnerAuthority>();
        var orderedOwners = authoritySet.Owners
            .OrderBy(OwnerInputOrderKey, StringComparer.Ordinal)
            .ToImmutableArray();
        var aggregateTargets = 0;
        var aggregateOwnerSymbols = 0;
        foreach (var owner in orderedOwners)
        {
            Require(owner is not null, CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Owner authority cannot contain null records.");
            var actualOwner = owner!;
            Require(!actualOwner.Targets.IsDefault
                    && !actualOwner.Targets.IsEmpty
                    && actualOwner.Targets.Length <= MaximumTargets,
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Owner authority rows require bounded target membership.");
            aggregateTargets = checked(aggregateTargets + actualOwner.Targets.Length);
            Require(aggregateTargets <= MaximumTargets,
                CampaignPlanningValidationCode.InvalidBound,
                "Aggregate owner target membership exceeds the finite planning bound.");
            foreach (var targetAuthority in actualOwner.Targets)
            {
                Require(targetAuthority is not null
                        && targetAuthority.Target is not null
                        && targetAuthority.Source is not null,
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Target owner authority cannot contain null facts.");
                var actualTargetAuthority = targetAuthority!;
                var symbol = actualTargetAuthority.Target!.SymbolRef;
                Require(supportedTargets.TryGetValue(symbol, out var accepted)
                        && Equals(accepted, actualTargetAuthority.Target)
                    && targetMembership.TryAdd(symbol, actualOwner),
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Selected owner authority must reference current supported targets without duplicate membership.");
            }

            Require(actualOwner.Targets.Any(target =>
                    violationParents.Contains(target.Target.SymbolRef)),
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Physical owner authority may contain only the exact closure of selected violation parents.");
        }

        Require(violationParents.All(targetMembership.ContainsKey),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner authority must cover every violation parent exactly once.");

        var allTargets = orderedOwners
            .SelectMany(owner => owner.Targets)
            .OrderBy(target => SymbolKey(target.Target.SymbolRef), StringComparer.Ordinal)
            .ToImmutableArray();
        foreach (var target in allTargets)
        {
            ValidateSourceAuthority(target.Source);
            sourceSession.BindSource(target.Target.SymbolRef, target.Source);
        }

        var physicalKeys = new HashSet<string>(StringComparer.Ordinal);
        var validated = ImmutableArray.CreateBuilder<ValidatedOwnerAuthority>();
        foreach (var owner in orderedOwners)
        {
            var targets = owner.Targets
                .OrderBy(target => SymbolKey(target.Target.SymbolRef), StringComparer.Ordinal)
                .ToImmutableArray();
            var key = BuildPhysicalOwnerKey(targets[0], sourceSession);
            Require(targets.All(target => BuildPhysicalOwnerKey(target, sourceSession) == key)
                    && physicalKeys.Add(key),
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Owner rows must be the exact partition of canonical physical-owner descriptors.");
            var expectedSymbols = targets.Select(target => target.Target.SymbolRef).ToImmutableArray();
            foreach (var target in targets)
            {
                aggregateOwnerSymbols = checked(aggregateOwnerSymbols + target.OwnerSymbolRefs.Length);
                Require(aggregateOwnerSymbols <= MaximumOwnerSymbols,
                    CampaignPlanningValidationCode.InvalidBound,
                    "Aggregate owner SymbolRef authority exceeds the finite planning bound.");
                ValidateOwnerSymbols(target, expectedSymbols);
            }

            validated.Add(new ValidatedOwnerAuthority(
                "campaign-owner." + Sha256(StrictUtf8.GetBytes(key)),
                key,
                targets,
                owner.AmbiguousOwner));
        }

        foreach (var target in allTargets)
        {
            ValidateSourceCorrelation(
                observations[TargetKey(target.Target.SymbolRef)],
                target,
                sourceSession);
        }

        foreach (var target in allTargets)
        {
            ValidateComponents(
                applicableComponentsByTarget,
                observations,
                target,
                sourceSession);
        }

        foreach (var target in allTargets.Where(target =>
                     target.ExecutableStyleProfile is not null))
        {
            ValidateStyleProfile(target.ExecutableStyleProfile!, target.ApplicableComponents);
        }

        return validated
            .OrderBy(owner => owner.PhysicalOrderKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void ValidateSourceAuthority(CampaignPlanningSourceAuthority source)
    {
        Require(Enum.IsDefined(source.Kind)
                && Enum.IsDefined(source.BlockState)
                && NonEmptySpan(source.ObservationDeclarationSpan)
                && NonEmptySpan(source.RequestedDeclarationSpan)
                && NonEmptySpan(source.CanonicalDeclarationSpan)
                && NonEmptySpan(source.OwnerSpan)
                && (source.DocumentationSpan is null || ValidSpan(source.DocumentationSpan.Value)),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Source authority uses an invalid vocabulary value or span.");
        RequireOpaqueIdentifier(
            source.AuthoritativeDeclarationId,
            nameof(source.AuthoritativeDeclarationId));
        RequireSha256(source.ContentSha256, nameof(source.ContentSha256));
        Require(source.RequestedDeclarationSpan.Start >= source.ObservationDeclarationSpan.Start
                && source.RequestedDeclarationSpan.End <= source.ObservationDeclarationSpan.End
                && source.CanonicalDeclarationSpan.Start >= source.ObservationDeclarationSpan.Start
                && source.CanonicalDeclarationSpan.End <= source.ObservationDeclarationSpan.End
                && source.OwnerSpan.Start >= source.ObservationDeclarationSpan.Start
                && source.OwnerSpan.End <= source.ObservationDeclarationSpan.End
                && source.RequestedDeclarationSpan.Start >= source.OwnerSpan.Start
                && source.RequestedDeclarationSpan.End <= source.OwnerSpan.End
                && source.CanonicalDeclarationSpan.Start >= source.OwnerSpan.Start
                && source.CanonicalDeclarationSpan.End <= source.OwnerSpan.End
                && (source.DocumentationSpan is null
                    || source.DocumentationSpan.Value.Start >= source.ObservationDeclarationSpan.Start
                        && source.DocumentationSpan.Value.End <= source.ObservationDeclarationSpan.End)
                && (source.BlockState == DocumentationBlockState.NoBlock
                    ? source.DocumentationSpan is null
                    : source.DocumentationSpan is { } documentation && NonEmptySpan(documentation)),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Source declaration, owner, documentation-span, and block-state facts are inconsistent.");
        switch (source)
        {
            case CampaignPlanningRepositorySourceAuthority repository:
                RequireCanonicalPath(repository.Path);
                RequireSha256(
                    repository.ObservedSourceTextSha256,
                    nameof(repository.ObservedSourceTextSha256));
                RequireSha256(
                    repository.PhysicalSourceCommitmentSha256,
                    nameof(repository.PhysicalSourceCommitmentSha256));
                Require(Enum.IsDefined(repository.Encoding),
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Repository source encoding is outside the closed vocabulary.");
                break;
            case CampaignPlanningGeneratedSourceAuthority generated:
                Require(generated.Kind is DocumentationPatchSourceKind.SourceGenerator
                        or DocumentationPatchSourceKind.ToolGenerated,
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Generated source authority requires a generated source kind.");
                RequireOpaqueIdentifier(generated.ProducerId, nameof(generated.ProducerId));
                RequireOpaqueIdentifier(generated.OutputId, nameof(generated.OutputId));
                break;
            default:
                throw Failure(CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Unknown source authority type.");
        }
    }

    private static void ValidateOwnerSymbols(
        CampaignPlanningTargetAuthority authority,
        ImmutableArray<SymbolRef> expectedSymbols)
    {
        Require(!authority.OwnerSymbolRefs.IsDefault && !authority.OwnerSymbolRefs.IsEmpty,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner SymbolRefs must be initialized and non-empty.");
        Require(authority.OwnerSymbolRefs.SequenceEqual(expectedSymbols)
                && expectedSymbols.Distinct().Count() == expectedSymbols.Length,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Every target must carry the exact canonical SymbolRef membership of its physical owner.");
    }

    private static void ValidateComponents(
        IReadOnlyDictionary<SymbolRef, ImmutableArray<ComponentClassification>> applicableComponentsByTarget,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
        CampaignPlanningTargetAuthority authority,
        CampaignPlanningSourceSessionIndex sourceSession)
    {
        Require(!authority.ApplicableComponents.IsDefault,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Applicable components must be initialized.");
        var expected = applicableComponentsByTarget.TryGetValue(
            authority.Target.SymbolRef,
            out var indexed)
            ? indexed
            : ImmutableArray<ComponentClassification>.Empty;
        Require(authority.ApplicableComponents.Length == expected.Length,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner authority must preserve the complete applicable-component closure.");
        for (var index = 0; index < expected.Length; index++)
        {
            var supplied = authority.ApplicableComponents[index];
            var accepted = expected[index];
            Require(supplied is not null
                    && supplied.Kind == accepted.ComponentKind
                    && supplied.Identity == accepted.Identity
                    && (supplied.Kind is ComponentKind.Parameter or ComponentKind.TypeParameter
                        ? !string.IsNullOrEmpty(supplied.Name)
                        : supplied.Name is null),
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Applicable components must retain canonical parent, kind, identity, order, and name shape.");
            var actualSupplied = supplied!;
            RequireIdentifier(actualSupplied.Identity, nameof(actualSupplied.Identity));
            if (actualSupplied.Name is not null)
            {
                RequireIdentifier(actualSupplied.Name, nameof(actualSupplied.Name));
            }

            var observation = observations[ComponentKey(
                accepted.ParentSymbolRef,
                accepted.ComponentKind,
                accepted.Identity)];
            var declaration = FindCorrelatedDeclaration(
                observation,
                authority,
                sourceSession);
            Require(actualSupplied.Name == declaration.ComponentLocalName,
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Applicable components must match the current declaration and source authority exactly.");
        }
    }

    private static void ValidateSourceCorrelation(
        DocumentationObservation observation,
        CampaignPlanningTargetAuthority authority,
        CampaignPlanningSourceSessionIndex sourceSession)
    {
        _ = FindCorrelatedDeclaration(observation, authority, sourceSession);
    }

    private static DocumentationDeclarationFact FindCorrelatedDeclaration(
        DocumentationObservation observation,
        CampaignPlanningTargetAuthority authority,
        CampaignPlanningSourceSessionIndex sourceSession)
    {
        var source = authority.Source;
        var declarations = observation.Declarations
            .Where(declaration => declaration.DeclarationId == source.AuthoritativeDeclarationId)
            .ToImmutableArray();
        Require(declarations.Length == 1
                && sourceSession.MatchesObservedSource(
                    authority.Target.SymbolRef.CompilationContextRef,
                    declarations[0].Source,
                    source)
                && declarations[0].BlockState == source.BlockState
                && declarations[0].DeclarationSpan == source.ObservationDeclarationSpan
                && declarations[0].DocumentationSpan == source.DocumentationSpan,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Exact source authority must correlate with one authoritative declaration and its source, span, documentation, and block facts.");
        return declarations[0];
    }

    private static PendingWorkItem BuildPendingItem(
        ValidatedOwnerAuthority owner,
        IReadOnlyDictionary<string, AuditRow> auditRows,
        ImmutableArray<CampaignPlanningViolationCause> causes)
    {
        var reasons = new HashSet<CampaignPlanningTerminalReason>();
        if (owner.AmbiguousOwner)
        {
            reasons.Add(CampaignPlanningTerminalReason.AmbiguousOwner);
        }

        if (owner.Targets.Length != 1
            || owner.Targets.Any(target => target.OwnerSymbolRefs.Length != 1
                || target.OwnerSymbolRefs[0] != target.Target.SymbolRef))
        {
            reasons.Add(CampaignPlanningTerminalReason.SharedOwner);
        }

        var targetFacts = owner.Targets
            .OrderBy(target => SymbolKey(target.Target.SymbolRef), StringComparer.Ordinal)
            .Select(authority =>
            {
                if (authority.MultiDeclarator)
                {
                    reasons.Add(CampaignPlanningTerminalReason.MultiDeclarator);
                }

                if (authority.PrimaryConstructorAlias)
                {
                    reasons.Add(CampaignPlanningTerminalReason.PrimaryConstructorAlias);
                }

                if (authority.PrimaryConstructor)
                {
                    reasons.Add(CampaignPlanningTerminalReason.PrimaryConstructor);
                }

                if (authority.Source.Kind != DocumentationPatchSourceKind.Repository)
                {
                    reasons.Add(CampaignPlanningTerminalReason.NonRepositorySource);
                }

                if (!authority.Source.Writable)
                {
                    reasons.Add(CampaignPlanningTerminalReason.NonWritableSource);
                }

                if (authority.Target.PrimaryKind != PrimarySymbolKind.Method)
                {
                    reasons.Add(CampaignPlanningTerminalReason.UnsupportedTargetKind);
                }

                if (authority.Source.BlockState is not (
                    DocumentationBlockState.NoBlock
                    or DocumentationBlockState.WhitespaceOnly
                    or DocumentationBlockState.WellFormed))
                {
                    reasons.Add(CampaignPlanningTerminalReason.UnsupportedBlockState);
                }

                var targetRow = auditRows[TargetKey(authority.Target.SymbolRef)];
                var eligible = IsM3Eligible(
                    owner.AmbiguousOwner,
                    owner.Targets,
                    authority);
                return new CampaignPlanningTargetFact(
                    authority.Target.SymbolRef,
                    authority.Target.PrimaryKind,
                    authority.Target.Origin,
                    authority.Source,
                    authority.Source.AuthoritativeDeclarationId,
                    authority.ApplicableComponents,
                    authority.OwnerSymbolRefs,
                    targetRow.Outcome,
                    targetRow.Reason,
                    targetRow.RowSha256,
                    eligible,
                    authority.ExecutableStyleProfile);
            }).ToImmutableArray();

        if (causes.Any(cause => cause.Reason == AuditReason.ForbiddenPresent))
        {
            reasons.Add(CampaignPlanningTerminalReason.UnsupportedRemoval);
        }

        var orderedReasons = reasons.OrderBy(reason => reason).ToImmutableArray();
        CampaignPlanningDisposition disposition;
        if (orderedReasons.IsEmpty)
        {
            Require(targetFacts.Length == 1
                    && targetFacts[0].M3Eligible
                    && targetFacts[0].StyleProfile is not null
                    && causes.All(cause => cause.Reason == AuditReason.RequiredAbsent),
                CampaignPlanningValidationCode.InvalidStyleAuthority,
                "Executable work requires one target-valid Style Profile and required-absent causes.");
            ValidateStyleProfile(
                targetFacts[0].StyleProfile!,
                targetFacts[0].ApplicableComponents);
            var capability = targetFacts[0].Source.BlockState == DocumentationBlockState.NoBlock
                ? CampaignPlanningEditCapability.Insert
                : CampaignPlanningEditCapability.Replace;
            disposition = new CampaignPlanningDisposition(
                CampaignPlanningDispositionKind.Executable,
                capability,
                null,
                []);
        }
        else
        {
            Require(targetFacts.All(target => target.StyleProfile is null),
                CampaignPlanningValidationCode.InvalidStyleAuthority,
                "Terminal work cannot carry an execution-only Style Profile.");
            disposition = new CampaignPlanningDisposition(
                CampaignPlanningDispositionKind.Terminal,
                null,
                orderedReasons[0],
                orderedReasons);
        }

        return new PendingWorkItem(
            owner.CanonicalOwnerRef,
            targetFacts,
            causes,
            disposition);
    }

    private static bool IsM3Eligible(
        bool ambiguousOwner,
        ImmutableArray<CampaignPlanningTargetAuthority> ownerTargets,
        CampaignPlanningTargetAuthority authority) =>
        ownerTargets.Length == 1
        && !ambiguousOwner
        && !authority.MultiDeclarator
        && !authority.PrimaryConstructor
        && !authority.PrimaryConstructorAlias
        && authority.OwnerSymbolRefs.Length == 1
        && authority.OwnerSymbolRefs[0] == authority.Target.SymbolRef
        && authority.Target.PrimaryKind == PrimarySymbolKind.Method
        && authority.Source.Kind == DocumentationPatchSourceKind.Repository
        && authority.Source.Writable
        && authority.Source.BlockState is DocumentationBlockState.NoBlock
            or DocumentationBlockState.WhitespaceOnly
            or DocumentationBlockState.WellFormed;

    private static void ValidateStyleProfile(
        DocumentationScribeStyleProfile profile,
        ImmutableArray<CampaignPlanningApplicableComponent> components)
    {
        RequireOpaqueIdentifier(profile.StyleProfileId, nameof(profile.StyleProfileId));
        RequireOpaqueIdentifier(profile.OutputLanguageId, nameof(profile.OutputLanguageId));
        Require(profile.Summary is not null && profile.Remarks is not null && profile.Exceptions is not null,
            CampaignPlanningValidationCode.InvalidStyleAuthority,
            "Style Profile text policies are required.");
        Require(!profile.ComponentPolicies.IsDefault
                && profile.ComponentPolicies.Length == components.Length,
            CampaignPlanningValidationCode.InvalidStyleAuthority,
            "Style Profile component policies must exactly match the target component closure.");
        for (var index = 0; index < components.Length; index++)
        {
            var policy = profile.ComponentPolicies[index];
            Require(policy is not null
                    && policy.ComponentIdentity == components[index].Identity
                    && Enum.IsDefined(policy.Disposition)
                    && policy.MaximumScalars >= 0,
                CampaignPlanningValidationCode.InvalidStyleAuthority,
                "Style Profile component policies must retain exact target component identity and order.");
        }

        var summary = profile.Summary!;
        var remarks = profile.Remarks!;
        var exceptions = profile.Exceptions!;
        Require(Enum.IsDefined(summary.Disposition)
                && Enum.IsDefined(remarks.Disposition)
                && Enum.IsDefined(exceptions.Disposition)
                && Enum.IsDefined(profile.InheritDocDisposition)
                && summary.MaximumScalars >= 0
                && remarks.MaximumScalars >= 0
                && exceptions.MaximumScalars >= 0
                && !profile.AllowedLiterals.IsDefault
                && !profile.ForbiddenLiterals.IsDefault
                && !profile.ClaimPolicies.IsDefault
                && profile.MaximumContentUnits > 0
                && profile.MaximumEvidenceRefsPerUnit > 0,
            CampaignPlanningValidationCode.InvalidStyleAuthority,
            "Style Profile content is outside the closed validated shape.");
        foreach (var literal in profile.AllowedLiterals.Concat(profile.ForbiddenLiterals))
        {
            RequireText(literal, nameof(literal));
        }

        foreach (var claim in profile.ClaimPolicies)
        {
            Require(claim is not null && !claim.AllowedAuthorities.IsDefault
                    && claim.AllowedAuthorities.All(Enum.IsDefined),
                CampaignPlanningValidationCode.InvalidStyleAuthority,
                "Style Profile claim policy is outside the closed vocabulary.");
            RequireIdentifier(claim!.ClaimCategoryId, nameof(claim.ClaimCategoryId));
        }
    }

    private static string ComputeExecutionCommitment(
        CampaignPlanningInput input,
        string auditSha256,
        ImmutableArray<PendingWorkItem> items)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-execution-commitment/v1");
        writer.Add("planning-contract", CampaignPlanningVocabulary.PlanningContractRevision);
        writer.Add("campaign-lineage", input.Snapshot.CampaignLineage);
        writer.Add("snapshot-binding", input.Snapshot.OpaqueSnapshotBinding);
        writer.Add("repository-commitment", input.Snapshot.RepositoryCommitmentSha256);
        writer.Add("input-commitment", input.Snapshot.InputCommitmentSha256);
        writer.Add("policy-authority", input.Snapshot.PolicyAuthorityCommitmentSha256);
        writer.Add("audit-result", auditSha256);
        writer.Add("target-profile", ClassificationVocabulary.GetId(input.Snapshot.TargetProfile));
        writer.Add("selection-policy", CampaignPlanningVocabulary.SelectionPolicy);
        writer.Add("ordering-policy", CampaignPlanningVocabulary.OrderingPolicy);
        AddExecutionPolicy(writer, input.ExecutionPolicy);
        writer.Add("relation-count", input.Classifications.Relations.Length);
        foreach (var relation in input.Classifications.Relations)
        {
            writer.Add("relation.kind", ClassificationVocabulary.GetId(relation.RelationKind));
            AddSymbolRef(writer, "relation.source", relation.SourceSymbolRef);
            AddSymbolRef(writer, "relation.target", relation.TargetSymbolRef);
        }
        var observationAuthorities = input.EvidenceAuthority
            .OrderBy(authority => ObservationKey(authority.Subject), StringComparer.Ordinal)
            .ToArray();
        writer.Add("observation-authority-count", observationAuthorities.Length);
        foreach (var authority in observationAuthorities)
        {
            writer.Add("observation-authority.subject", ObservationKey(authority.Subject));
            writer.Add(
                "observation-authority.sha256",
                authority.ObservationAuthorityCommitmentSha256);
        }
        writer.Add("work-count", items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            writer.Add("work-index", index);
            AddPendingItem(writer, items[index]);
        }

        return writer.Complete();
    }

    private static string ComputeWorkItemKey(
        string executionCommitment,
        PendingWorkItem item)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-work-item/v1");
        writer.Add("execution-commitment", executionCommitment);
        AddPendingItem(writer, item);
        return writer.Complete();
    }

    private static void AddExecutionPolicy(
        CampaignPlanningCommitmentWriter writer,
        CampaignPlanningExecutionPolicy policy)
    {
        AddContentAuthority(writer, "proposal", policy.ProposalContract);
        AddContentAuthority(writer, "agent", policy.AgentProtocol);
        AddContentAuthority(writer, "context", policy.ContextSelectionPolicy);
        AddContentAuthority(writer, "tools", policy.ToolPolicyAndRegistry);
        AddContentAuthority(writer, "provider", policy.ProviderModelRequestProfile);
        AddContentAuthority(writer, "retry", policy.RetryPolicy);
        AddContentAuthority(writer, "m2", policy.M2ProjectionPolicy);
        AddContentAuthority(writer, "product", policy.ProductContractRevision);
        var limits = policy.ScribeRunLimits;
        writer.Add("scribe.max-context-refs", limits.MaximumContextReferences);
        writer.Add("scribe.max-context-bytes", limits.MaximumContextUtf8Bytes);
        writer.Add("scribe.max-evidence-refs", limits.MaximumEvidenceReferences);
        writer.Add("scribe.max-evidence-bytes", limits.MaximumEvidenceUtf8Bytes);
        writer.Add("scribe.max-provider-requests", limits.MaximumProviderRequests);
        writer.Add("scribe.max-tool-rounds", limits.MaximumToolRounds);
        writer.Add("scribe.max-tool-calls", limits.MaximumToolCalls);
        writer.Add("scribe.max-attempts", limits.MaximumAttempts);
        writer.Add("scribe.max-input-tokens", limits.MaximumInputTokens);
        writer.Add("scribe.max-uncached-input-tokens", limits.MaximumUncachedInputTokens);
        writer.Add("scribe.max-output-tokens", limits.MaximumOutputTokens);
        writer.Add("scribe.max-cost", limits.MaximumCostMicrounits);
        writer.Add("scribe.max-elapsed", limits.MaximumElapsedMilliseconds);
        var budget = policy.CampaignBudget;
        writer.Add("campaign.max-blocks", budget.MaximumBlocks);
        writer.Add("campaign.max-files", budget.MaximumChangedFiles);
        writer.Add("campaign.max-patch-bytes", budget.MaximumPatchBytes);
        writer.Add("campaign.max-provider-requests", budget.MaximumProviderRequests);
        writer.Add("campaign.max-attempts-target", budget.MaximumAttemptsPerTarget);
        writer.Add("campaign.max-input-tokens", budget.MaximumInputTokens);
        writer.Add("campaign.max-uncached-input-tokens", budget.MaximumUncachedInputTokens);
        writer.Add("campaign.max-output-tokens", budget.MaximumOutputTokens);
        writer.Add("campaign.max-cost", budget.MaximumCostMicrounits);
        writer.Add("campaign.max-elapsed", budget.MaximumElapsedMilliseconds);
        writer.Add("campaign.max-candidates", budget.MaximumCandidatesPerBlock);
        writer.Add("campaign.cost-enforced", budget.CostEnforced);
        writer.AddOptional("campaign.cost-currency", budget.CostCurrency);
        writer.Add("campaign.cost-rate.present", budget.CostRatePolicy is not null);
        if (budget.CostRatePolicy is not null)
        {
            AddContentAuthority(writer, "campaign.cost-rate", budget.CostRatePolicy);
        }
    }

    private static void AddContentAuthority(
        CampaignPlanningCommitmentWriter writer,
        string label,
        CampaignPlanningContentAuthority authority)
    {
        writer.Add(label + ".family", CampaignPlanningContentAuthority.GetContentFamilyId(authority.Family));
        writer.Add(label + ".id", authority.Id);
        writer.Add(label + ".sha256", authority.ContentSha256);
    }

    private static void AddPendingItem(
        CampaignPlanningCommitmentWriter writer,
        PendingWorkItem item)
    {
        writer.Add("owner-equivalence", item.OwnerEquivalenceRef);
        writer.Add("target-count", item.Targets.Length);
        foreach (var target in item.Targets)
        {
            AddSymbolRef(writer, "target", target.SymbolRef);
            writer.Add("target.primary-kind", ClassificationVocabulary.GetId(target.PrimaryKind));
            writer.Add("target.origin", ClassificationVocabulary.GetId(target.Origin));
            AddSource(writer, target.Source);
            writer.Add("target.owner-symbol-count", target.OwnerSymbolRefs.Length);
            foreach (var ownerSymbol in target.OwnerSymbolRefs)
            {
                AddSymbolRef(writer, "target.owner-symbol", ownerSymbol);
            }

            writer.Add("target.component-count", target.ApplicableComponents.Length);
            foreach (var component in target.ApplicableComponents)
            {
                writer.Add("target.component.kind", ClassificationVocabulary.GetId(component.Kind));
                writer.Add("target.component.identity", component.Identity);
                writer.AddOptional("target.component.name", component.Name);
            }

            writer.Add("target.audit-outcome", AuditVocabulary.GetId(target.AuditOutcome));
            writer.Add("target.audit-reason", AuditVocabulary.GetId(target.AuditReason));
            writer.Add("target.audit-row", target.AuditRowSha256);
            writer.Add("target.m3-eligible", target.M3Eligible);
            writer.Add("target.style.present", target.StyleProfile is not null);
            if (target.StyleProfile is not null)
            {
                AddStyleProfile(writer, target.StyleProfile);
            }
        }

        writer.Add("violation-count", item.Causes.Length);
        foreach (var cause in item.Causes)
        {
            AddSymbolRef(writer, "violation.parent", cause.ParentSymbolRef);
            writer.Add("violation.component.present", cause.ComponentKind is not null);
            if (cause.ComponentKind is not null)
            {
                writer.Add("violation.component.kind", ClassificationVocabulary.GetId(cause.ComponentKind.Value));
                writer.Add("violation.component.identity", cause.ComponentIdentity!);
            }

            writer.Add("violation.reason", AuditVocabulary.GetId(cause.Reason));
            writer.Add("violation.audit-row", cause.AuditRowSha256);
        }

        writer.Add("disposition.kind", CampaignPlanningVocabulary.GetId(item.Disposition.Kind));
        writer.Add("disposition.edit.present", item.Disposition.EditCapability is not null);
        if (item.Disposition.EditCapability is not null)
        {
            writer.Add("disposition.edit", CampaignPlanningVocabulary.GetId(item.Disposition.EditCapability.Value));
        }

        writer.Add("disposition.reason-count", item.Disposition.TerminalReasons.Length);
        foreach (var reason in item.Disposition.TerminalReasons)
        {
            writer.Add("disposition.reason", CampaignPlanningVocabulary.GetId(reason));
        }

        writer.Add("disposition.primary.present", item.Disposition.PrimaryTerminalReason is not null);
        if (item.Disposition.PrimaryTerminalReason is not null)
        {
            writer.Add("disposition.primary", CampaignPlanningVocabulary.GetId(item.Disposition.PrimaryTerminalReason.Value));
        }
    }

    private static void AddSource(
        CampaignPlanningCommitmentWriter writer,
        CampaignPlanningSourceAuthority source)
    {
        writer.Add("source.kind", SourceKindId(source.Kind));
        writer.Add("source.authoritative-declaration", source.AuthoritativeDeclarationId);
        writer.Add("source.sha256", source.ContentSha256);
        writer.Add("source.writable", source.Writable);
        AddSpan(writer, "source.observation-declaration-span", source.ObservationDeclarationSpan);
        AddSpan(writer, "source.requested-span", source.RequestedDeclarationSpan);
        AddSpan(writer, "source.canonical-span", source.CanonicalDeclarationSpan);
        AddSpan(writer, "source.owner-span", source.OwnerSpan);
        writer.Add("source.documentation-span.present", source.DocumentationSpan is not null);
        if (source.DocumentationSpan is not null)
        {
            AddSpan(writer, "source.documentation-span", source.DocumentationSpan.Value);
        }

        writer.Add("source.block-state", DocumentationObservationVocabulary.GetId(source.BlockState));
        switch (source)
        {
            case CampaignPlanningRepositorySourceAuthority repository:
                writer.Add("source.repository.path", repository.Path);
                writer.Add(
                    "source.repository.observed-text-sha256",
                    repository.ObservedSourceTextSha256);
                writer.Add(
                    "source.repository.physical-source-sha256",
                    repository.PhysicalSourceCommitmentSha256);
                writer.Add("source.repository.encoding", EncodingId(repository.Encoding));
                break;
            case CampaignPlanningGeneratedSourceAuthority generated:
                writer.Add("source.generated.producer", generated.ProducerId);
                writer.Add("source.generated.output", generated.OutputId);
                break;
        }
    }

    private static void AddStyleProfile(
        CampaignPlanningCommitmentWriter writer,
        DocumentationScribeStyleProfile profile)
    {
        writer.Add("style.id", profile.StyleProfileId);
        writer.Add("style.language", profile.OutputLanguageId);
        AddTextPolicy(writer, "style.summary", profile.Summary);
        AddTextPolicy(writer, "style.remarks", profile.Remarks);
        AddTextPolicy(writer, "style.exceptions", profile.Exceptions);
        writer.Add("style.component-count", profile.ComponentPolicies.Length);
        foreach (var component in profile.ComponentPolicies)
        {
            writer.Add("style.component.identity", component.ComponentIdentity);
            writer.Add("style.component.disposition", DocumentationScribeVocabulary.GetId(component.Disposition));
            writer.Add("style.component.maximum-scalars", component.MaximumScalars);
        }

        writer.Add("style.inherit-doc", DocumentationScribeVocabulary.GetId(profile.InheritDocDisposition));
        writer.Add("style.allowed-count", profile.AllowedLiterals.Length);
        foreach (var literal in profile.AllowedLiterals)
        {
            writer.Add("style.allowed", literal);
        }

        writer.Add("style.forbidden-count", profile.ForbiddenLiterals.Length);
        foreach (var literal in profile.ForbiddenLiterals)
        {
            writer.Add("style.forbidden", literal);
        }

        writer.Add("style.claim-count", profile.ClaimPolicies.Length);
        foreach (var claim in profile.ClaimPolicies)
        {
            writer.Add("style.claim.id", claim.ClaimCategoryId);
            writer.Add("style.claim.complete", claim.CompleteEvidenceRequired);
            writer.Add("style.claim.authority-count", claim.AllowedAuthorities.Length);
            foreach (var authority in claim.AllowedAuthorities)
            {
                writer.Add("style.claim.authority", DocumentationScribeVocabulary.GetId(authority));
            }
        }

        writer.Add("style.maximum-content-units", profile.MaximumContentUnits);
        writer.Add("style.maximum-evidence-refs", profile.MaximumEvidenceRefsPerUnit);
    }

    private static void AddTextPolicy(
        CampaignPlanningCommitmentWriter writer,
        string label,
        DocumentationScribeTextPolicy policy)
    {
        writer.Add(label + ".disposition", DocumentationScribeVocabulary.GetId(policy.Disposition));
        writer.Add(label + ".maximum-scalars", policy.MaximumScalars);
    }

    private static void AddSymbolRef(
        CampaignPlanningCommitmentWriter writer,
        string label,
        SymbolRef symbol)
    {
        writer.Add(label + ".context", symbol.CompilationContextRef);
        writer.Add(label + ".documentation-id", symbol.DocumentationCommentId);
    }

    private static void AddSpan(
        CampaignPlanningCommitmentWriter writer,
        string label,
        Utf16Span span)
    {
        writer.Add(label + ".start", span.Start);
        writer.Add(label + ".end", span.End);
    }

    private static CampaignPlanningSummary BuildSummary(
        ImmutableArray<CampaignPlanningWorkItem> items)
    {
        var reasonCounts = Enum.GetValues<CampaignPlanningTerminalReason>()
            .Select(reason => new CampaignPlanningTerminalReasonCount(
                reason,
                items.Count(item => item.Disposition.TerminalReasons.Contains(reason))))
            .Where(entry => entry.Count > 0)
            .ToImmutableArray();
        return new CampaignPlanningSummary(
            items.Length,
            items.Count(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Executable),
            items.Count(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Terminal),
            items.Count(item => item.Targets.All(target =>
                target.Source.Kind == DocumentationPatchSourceKind.Repository)),
            items.Count(item => item.Targets.Any(target =>
                target.Source.Kind != DocumentationPatchSourceKind.Repository
                || !target.Source.Writable)),
            reasonCounts);
    }

    private static void AppendKey(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    private static void AppendKey(StringBuilder builder, int value) =>
        builder.Append(value.ToString("D10", System.Globalization.CultureInfo.InvariantCulture));

    private static string OwnerInputOrderKey(CampaignPlanningOwnerAuthority? owner)
    {
        if (owner is null || owner.Targets.IsDefault)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var target in owner.Targets
                     .Where(target => target?.Target is not null)
                     .OrderBy(target => SymbolKey(target.Target.SymbolRef), StringComparer.Ordinal))
        {
            AppendKey(builder, SymbolKey(target.Target.SymbolRef));
        }

        return builder.ToString();
    }

    private static string BuildPhysicalOwnerKey(
        CampaignPlanningTargetAuthority target,
        CampaignPlanningSourceSessionIndex sourceSession)
    {
        var builder = new StringBuilder();
        AppendKey(builder, sourceSession.PhysicalSourceKey(target.Target.SymbolRef, target.Source));
        AppendKey(builder, target.Source.OwnerSpan.Start);
        AppendKey(builder, target.Source.OwnerSpan.End);
        return builder.ToString();
    }

    private static EvidenceSubject ReadEvidenceSubject(JsonElement value)
    {
        if (value.TryGetProperty("parentSymbolRef", out var parent))
        {
            var symbol = ReadSymbolRef(parent);
            return EvidenceInput.ComponentSubject(
                symbol.CompilationContextRef,
                symbol.DocumentationCommentId,
                ReadComponentKind(value.GetProperty("componentKind").GetString()),
                value.GetProperty("identity").GetString()!);
        }

        var target = ReadSymbolRef(value);
        return EvidenceInput.TargetSubject(
            target.CompilationContextRef,
            target.DocumentationCommentId);
    }

    private static string AuditAuthorityRoleId(DocumentationAuthorityRole role) => role switch
    {
        DocumentationAuthorityRole.Ordinary => "ordinary",
        DocumentationAuthorityRole.PartialTypePart => "partial-type-part",
        DocumentationAuthorityRole.PartialMemberImplementing => "partial-member-implementing",
        DocumentationAuthorityRole.PartialMemberDefiningFallback => "partial-member-defining-fallback",
        _ => throw Failure(CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Evidence authority role is outside the closed vocabulary."),
    };

    private static string AuditBlockStateId(DocumentationBlockState state) => state switch
    {
        DocumentationBlockState.NoBlock => "no-block",
        DocumentationBlockState.WhitespaceOnly => "whitespace-only",
        DocumentationBlockState.WellFormed => "well-formed",
        DocumentationBlockState.Malformed => "malformed",
        _ => throw Failure(CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Evidence block state is outside the closed vocabulary."),
    };

    private static string AuditComponentMatchId(DocumentationComponentMatch match) => match switch
    {
        DocumentationComponentMatch.Present => "present",
        DocumentationComponentMatch.Absent => "absent",
        _ => throw Failure(CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Evidence component match is outside the closed vocabulary."),
    };

    private static bool TargetClassificationMatches(TargetClassification target, JsonElement value)
    {
        var symbol = ReadSymbolRef(value.GetProperty("symbolRef"));
        var traits = value.GetProperty("traits")
            .EnumerateArray()
            .Select(trait => trait.GetString())
            .ToArray();
        return symbol == target.SymbolRef
            && value.GetProperty("primaryKind").GetString() == ClassificationVocabulary.GetId(target.PrimaryKind)
            && traits.SequenceEqual(target.Traits.Select(ClassificationVocabulary.GetId), StringComparer.Ordinal)
            && value.GetProperty("origin").GetString() == ClassificationVocabulary.GetId(target.Origin)
            && value.GetProperty("supportStatus").GetString() == ClassificationVocabulary.GetId(target.SupportStatus)
            && SkipReasonMatches(value, target.SkipReason);
    }

    private static bool ComponentClassificationMatches(ComponentClassification component, JsonElement value)
    {
        var parent = ReadSymbolRef(value.GetProperty("parentSymbolRef"));
        return parent == component.ParentSymbolRef
            && value.GetProperty("componentKind").GetString() == ClassificationVocabulary.GetId(component.ComponentKind)
            && value.GetProperty("identity").GetString() == component.Identity
            && value.GetProperty("origin").GetString() == ClassificationVocabulary.GetId(component.Origin)
            && value.GetProperty("supportStatus").GetString() == ClassificationVocabulary.GetId(component.SupportStatus)
            && SkipReasonMatches(value, component.SkipReason);
    }

    private static bool UnresolvedClassificationMatches(
        UnresolvedClassification unresolved,
        JsonElement value) =>
        value.GetProperty("compilationContextRef").GetString() == unresolved.CompilationContextRef
        && value.GetProperty("origin").GetString() == ClassificationVocabulary.GetId(unresolved.Origin)
        && value.GetProperty("supportStatus").GetString()
            == ClassificationVocabulary.GetId(unresolved.SupportStatus)
        && value.GetProperty("skipReason").GetString()
            == ClassificationVocabulary.GetId(unresolved.SkipReason)
        && CandidateLocatorMatches(unresolved.CandidateLocator, value.GetProperty("candidateLocator"));

    private static bool CandidateLocatorMatches(CandidateLocator locator, JsonElement value) => locator switch
    {
        RepositoryCandidateLocator expected =>
            LocatorPayloadMatches(value, "repository", "path", expected.Path, null, null, expected.Span),
        GeneratedSourceCandidateLocator expected =>
            LocatorPayloadMatches(value, "generatedSource", "generatorId", expected.GeneratorId,
                "hintNameId", expected.HintNameId, expected.Span),
        ToolGeneratedCandidateLocator expected =>
            LocatorPayloadMatches(value, "toolGenerated", "producerId", expected.ProducerId,
                "outputId", expected.OutputId, expected.Span),
        SyntheticCandidateLocator expected =>
            value.TryGetProperty("synthetic", out var synthetic)
            && synthetic.GetProperty("fixtureId").GetString() == expected.FixtureId,
        _ => false,
    };

    private static string UnresolvedKey(UnresolvedClassification value) =>
        value.CompilationContextRef + "\u001f" + CandidateLocatorKey(value.CandidateLocator);

    private static string UnresolvedKey(JsonElement value) =>
        value.GetProperty("compilationContextRef").GetString()
        + "\u001f"
        + CandidateLocatorKey(value.GetProperty("candidateLocator"));

    private static string CandidateLocatorKey(CandidateLocator value)
    {
        var builder = new StringBuilder();
        switch (value)
        {
            case RepositoryCandidateLocator repository:
                AddLocatorKey(builder, 0, repository.Path, null, repository.Span);
                break;
            case GeneratedSourceCandidateLocator generated:
                AddLocatorKey(builder, 1, generated.GeneratorId, generated.HintNameId, generated.Span);
                break;
            case ToolGeneratedCandidateLocator generated:
                AddLocatorKey(builder, 2, generated.ProducerId, generated.OutputId, generated.Span);
                break;
            case SyntheticCandidateLocator synthetic:
                AddLocatorKey(builder, 3, synthetic.FixtureId, null, null);
                break;
        }

        return builder.ToString();
    }

    private static string CandidateLocatorKey(JsonElement value)
    {
        var builder = new StringBuilder();
        if (value.TryGetProperty("repository", out var repository))
        {
            AddLocatorKey(builder, 0, repository.GetProperty("path").GetString()!, null, ReadOptionalSpan(repository));
        }
        else if (value.TryGetProperty("generatedSource", out var generatedSource))
        {
            AddLocatorKey(
                builder,
                1,
                generatedSource.GetProperty("generatorId").GetString()!,
                generatedSource.GetProperty("hintNameId").GetString(),
                ReadOptionalSpan(generatedSource));
        }
        else if (value.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            AddLocatorKey(
                builder,
                2,
                toolGenerated.GetProperty("producerId").GetString()!,
                toolGenerated.GetProperty("outputId").GetString(),
                ReadOptionalSpan(toolGenerated));
        }
        else
        {
            AddLocatorKey(
                builder,
                3,
                value.GetProperty("synthetic").GetProperty("fixtureId").GetString()!,
                null,
                null);
        }

        return builder.ToString();
    }

    private static void AddLocatorKey(
        StringBuilder builder,
        int rank,
        string first,
        string? second,
        Utf16Span? span)
    {
        AppendKey(builder, rank);
        AppendKey(builder, first);
        AppendKey(builder, second ?? string.Empty);
        AppendKey(builder, span is null ? 0 : 1);
        AppendKey(builder, span?.Start ?? 0);
        AppendKey(builder, span?.End ?? 0);
    }

    private static Utf16Span? ReadOptionalSpan(JsonElement value) =>
        value.TryGetProperty("span", out var span)
            ? DocumentationObservationInput.Span(
                span.GetProperty("start").GetInt32(),
                span.GetProperty("end").GetInt32())
            : null;

    private static bool LocatorPayloadMatches(
        JsonElement value,
        string kind,
        string firstName,
        string firstValue,
        string? secondName,
        string? secondValue,
        Utf16Span? expectedSpan)
    {
        if (!value.TryGetProperty(kind, out var payload)
            || payload.GetProperty(firstName).GetString() != firstValue
            || (secondName is not null && payload.GetProperty(secondName).GetString() != secondValue))
        {
            return false;
        }

        if (expectedSpan is null)
        {
            return !payload.TryGetProperty("span", out _);
        }

        return payload.TryGetProperty("span", out var span)
            && span.GetProperty("start").GetInt32() == expectedSpan.Value.Start
            && span.GetProperty("end").GetInt32() == expectedSpan.Value.End;
    }

    private static bool SkipReasonMatches(JsonElement value, SkipReason? expected)
    {
        var actual = value.TryGetProperty("skipReason", out var skip)
            ? skip.GetString()
            : null;
        return actual == (expected is null ? null : ClassificationVocabulary.GetId(expected.Value));
    }

    private static SymbolRef ReadSymbolRef(JsonElement value) => new(
        value.GetProperty("compilationContextRef").GetString()!,
        value.GetProperty("documentationCommentId").GetString()!);

    private static AuditOutcome ReadAuditOutcome(string? value) => value switch
    {
        "audit.outcome.compliant" => AuditOutcome.Compliant,
        "audit.outcome.violation" => AuditOutcome.Violation,
        "audit.outcome.skipped" => AuditOutcome.Skipped,
        _ => throw Failure(CampaignPlanningValidationCode.InvalidAuditAuthority,
            "Audit outcome is outside the closed vocabulary."),
    };

    private static AuditReason ReadAuditReason(string? value) => value switch
    {
        "audit.reason.required-present" => AuditReason.RequiredPresent,
        "audit.reason.required-absent" => AuditReason.RequiredAbsent,
        "audit.reason.optional-present" => AuditReason.OptionalPresent,
        "audit.reason.optional-absent" => AuditReason.OptionalAbsent,
        "audit.reason.forbidden-present" => AuditReason.ForbiddenPresent,
        "audit.reason.forbidden-absent" => AuditReason.ForbiddenAbsent,
        "audit.reason.classification-skipped" => AuditReason.ClassificationSkipped,
        "audit.reason.policy-conflict" => AuditReason.PolicyConflict,
        "audit.reason.policy-unavailable" => AuditReason.PolicyUnavailable,
        "audit.reason.documentation-unavailable" => AuditReason.DocumentationUnavailable,
        "audit.reason.documentation-unavailable.malformed-xml" => AuditReason.DocumentationUnavailableMalformedXml,
        "audit.reason.evidence-incomplete" => AuditReason.EvidenceIncomplete,
        _ => throw Failure(CampaignPlanningValidationCode.InvalidAuditAuthority,
            "Audit reason is outside the closed vocabulary."),
    };

    private static ComponentKind ReadComponentKind(string? value) => value switch
    {
        "component.parameter" => ComponentKind.Parameter,
        "component.type-parameter" => ComponentKind.TypeParameter,
        "component.return" => ComponentKind.Return,
        "component.value" => ComponentKind.Value,
        "component.accessor.get" => ComponentKind.AccessorGet,
        "component.accessor.set" => ComponentKind.AccessorSet,
        "component.accessor.init" => ComponentKind.AccessorInit,
        "component.accessor.add" => ComponentKind.AccessorAdd,
        "component.accessor.remove" => ComponentKind.AccessorRemove,
        "component.backing-field" => ComponentKind.BackingField,
        "component.synthesized.record-positional-property" => ComponentKind.SynthesizedRecordPositionalProperty,
        "component.synthesized.implicit-constructor" => ComponentKind.SynthesizedImplicitConstructor,
        "component.synthesized.record-copy-constructor" => ComponentKind.SynthesizedRecordCopyConstructor,
        "component.synthesized.delegate-invoke" => ComponentKind.SynthesizedDelegateInvoke,
        "component.synthesized.delegate-begin-invoke" => ComponentKind.SynthesizedDelegateBeginInvoke,
        "component.synthesized.delegate-end-invoke" => ComponentKind.SynthesizedDelegateEndInvoke,
        "component.unknown" => ComponentKind.Unknown,
        _ => throw Failure(CampaignPlanningValidationCode.InvalidAuditAuthority,
            "Audit component kind is outside the closed vocabulary."),
    };

    private static string ObservationId(DocumentationObservationValue value) =>
        DocumentationObservationVocabulary.GetId(value);

    private static string ObservationKey(DocumentationObservationSubject subject) =>
        subject.ComponentKind is null
            ? TargetKey(subject.ParentSymbolRef)
            : ComponentKey(subject.ParentSymbolRef, subject.ComponentKind.Value, subject.ComponentIdentity!);

    private static string TargetKey(SymbolRef symbol) => "t\u001f" + SymbolKey(symbol);

    private static string ComponentKey(SymbolRef parent, ComponentKind kind, string identity) =>
        "c\u001f" + SymbolKey(parent) + "\u001f" + ClassificationVocabulary.GetId(kind) + "\u001f" + identity;

    private static string SymbolKey(SymbolRef symbol) =>
        symbol.CompilationContextRef + "\u001f" + symbol.DocumentationCommentId;

    private static int CompareSymbolRef(SymbolRef left, SymbolRef right)
    {
        var comparison = StringComparer.Ordinal.Compare(
            left.CompilationContextRef,
            right.CompilationContextRef);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(
                left.DocumentationCommentId,
                right.DocumentationCommentId);
    }

    private static bool IsApplicableComponent(ComponentKind kind) => kind is
        ComponentKind.TypeParameter or ComponentKind.Parameter or ComponentKind.Return or ComponentKind.Value;

    private static int ComponentRank(ComponentKind kind) => kind switch
    {
        ComponentKind.TypeParameter => 0,
        ComponentKind.Parameter => 1,
        ComponentKind.Return => 2,
        ComponentKind.Value => 3,
        _ => int.MaxValue,
    };

    private static DocumentationPatchSourceKind MapSourceKind(DocumentationSourceKind kind) => kind switch
    {
        DocumentationSourceKind.Repository => DocumentationPatchSourceKind.Repository,
        DocumentationSourceKind.SourceGenerator => DocumentationPatchSourceKind.SourceGenerator,
        DocumentationSourceKind.ToolGenerated => DocumentationPatchSourceKind.ToolGenerated,
        _ => throw Failure(CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Observation source kind is outside the closed vocabulary."),
    };

    private static string SourceKindId(DocumentationPatchSourceKind kind) => kind switch
    {
        DocumentationPatchSourceKind.Repository => "source.repository",
        DocumentationPatchSourceKind.SourceGenerator => "source.source-generator",
        DocumentationPatchSourceKind.ToolGenerated => "source.tool-generated",
        _ => throw Failure(CampaignPlanningValidationCode.InvalidVocabulary,
            "Source kind is outside the closed vocabulary."),
    };

    private static string EncodingId(DocumentationPatchRepositoryEncoding encoding) => encoding switch
    {
        DocumentationPatchRepositoryEncoding.Utf8 => "encoding.utf8",
        DocumentationPatchRepositoryEncoding.Utf8Bom => "encoding.utf8-bom",
        DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "encoding.utf16le-bom",
        DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "encoding.utf16be-bom",
        _ => throw Failure(CampaignPlanningValidationCode.InvalidVocabulary,
            "Repository encoding is outside the closed vocabulary."),
    };

    private static bool ValidSpan(Utf16Span span) => span.Start >= 0 && span.End >= span.Start;

    private static bool NonEmptySpan(Utf16Span span) => span.Start >= 0 && span.End > span.Start;

    private static void RequireCanonicalPath(string? path)
    {
        RequireText(path, nameof(path));
        Require(path!.Length <= 1_024
                && !path.StartsWith("/", StringComparison.Ordinal)
                && !path.Contains('\\', StringComparison.Ordinal)
                && !(path.Length >= 2 && path[1] == ':')
                && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".."),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Repository paths must use canonical repository-relative slash form.");
    }

    private static void RequireIdentifier(string? value, string name)
    {
        RequireText(value, name);
        Require(value!.Length <= MaximumIdentifierScalars
                && !value.Any(char.IsWhiteSpace),
            CampaignPlanningValidationCode.InvalidBound,
            $"{name} must be a bounded non-whitespace identifier.");
    }

    private static void RequireOpaqueIdentifier(string? value, string name)
    {
        RequireText(value, name);
        Require(value!.Length <= MaximumIdentifierScalars
                && char.IsAsciiLetterOrDigit(value[0])
                && value.AsSpan(1).IndexOfAnyExcept(
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:-") < 0,
            CampaignPlanningValidationCode.InvalidBound,
            $"{name} must use the bounded non-path opaque identifier grammar.");
    }

    private static void RequireText(string? value, string name)
    {
        Require(!string.IsNullOrEmpty(value)
                && value.Length <= 16_384
                && !value.Any(char.IsControl),
            CampaignPlanningValidationCode.InvalidBound,
            $"{name} must be initialized, bounded, and free of controls.");
        try
        {
            _ = StrictUtf8.GetByteCount(value!);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(CampaignPlanningValidationCode.InvalidBound,
                $"{name} contains invalid UTF-16.");
        }
    }

    private static void RequireSha256(string? value, string name) =>
        Require(value is { Length: 64 }
                && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0,
            CampaignPlanningValidationCode.InvalidCommitment,
            $"{name} must be a lowercase SHA-256 value.");

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Require(
        bool condition,
        CampaignPlanningValidationCode code,
        string message)
    {
        if (!condition)
        {
            throw Failure(code, message);
        }
    }

    private static CampaignPlanningValidationException Failure(
        CampaignPlanningValidationCode code,
        string message) => new(code, message);

    private sealed record AuditRow(
        string SubjectKey,
        SymbolRef ParentSymbolRef,
        ComponentKind? ComponentKind,
        string? ComponentIdentity,
        AuditOutcome Outcome,
        AuditReason Reason,
        string RowSha256,
        JsonElement Classification,
        string? ObservationId,
        ImmutableArray<string> EvidenceIds,
        JsonElement? EvidenceAuthority,
        JsonElement? ObservationSubject,
        JsonElement EvidenceBundle);

    private sealed record ValidatedOwnerAuthority(
        string CanonicalOwnerRef,
        string PhysicalOrderKey,
        ImmutableArray<CampaignPlanningTargetAuthority> Targets,
        bool AmbiguousOwner);

    private sealed record ValidatedObservationAuthority(
        IReadOnlyDictionary<string, DocumentationObservation> Observations,
        IReadOnlyDictionary<string, CampaignPlanningObservationProjectionResult> Projections);

    private sealed record PendingWorkItem(
        string OwnerEquivalenceRef,
        ImmutableArray<CampaignPlanningTargetFact> Targets,
        ImmutableArray<CampaignPlanningViolationCause> Causes,
        CampaignPlanningDisposition Disposition);

    private sealed class PendingWorkItemComparer : IComparer<PendingWorkItem>
    {
        internal static PendingWorkItemComparer Instance { get; } = new();

        public int Compare(PendingWorkItem? left, PendingWorkItem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var comparison = CompareTargets(left.Targets, right.Targets);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                left.OwnerEquivalenceRef,
                right.OwnerEquivalenceRef);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareCauses(left.Causes, right.Causes);
            return comparison != 0
                ? comparison
                : CompareDisposition(left.Disposition, right.Disposition);
        }

        private static int CompareTargets(
            ImmutableArray<CampaignPlanningTargetFact> left,
            ImmutableArray<CampaignPlanningTargetFact> right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = CompareTarget(left[index], right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int CompareTarget(
            CampaignPlanningTargetFact left,
            CampaignPlanningTargetFact right)
        {
            var comparison = CompareSource(left.Source, right.Source);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareSymbolRef(left.SymbolRef, right.SymbolRef);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                ClassificationVocabulary.GetId(left.PrimaryKind),
                ClassificationVocabulary.GetId(right.PrimaryKind));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                ClassificationVocabulary.GetId(left.Origin),
                ClassificationVocabulary.GetId(right.Origin));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareSymbols(left.OwnerSymbolRefs, right.OwnerSymbolRefs);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareComponents(left.ApplicableComponents, right.ApplicableComponents);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                AuditVocabulary.GetId(left.AuditOutcome),
                AuditVocabulary.GetId(right.AuditOutcome));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                AuditVocabulary.GetId(left.AuditReason),
                AuditVocabulary.GetId(right.AuditReason));
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.AuditRowSha256, right.AuditRowSha256);
        }

        private static int CompareSource(
            CampaignPlanningSourceAuthority left,
            CampaignPlanningSourceAuthority right)
        {
            var comparison = left.Kind.CompareTo(right.Kind);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = (left, right) switch
            {
                (CampaignPlanningRepositorySourceAuthority first,
                    CampaignPlanningRepositorySourceAuthority second) =>
                    CompareRepositorySource(first, second),
                (CampaignPlanningGeneratedSourceAuthority first,
                    CampaignPlanningGeneratedSourceAuthority second) =>
                    CompareGeneratedSource(first, second),
                _ => StringComparer.Ordinal.Compare(left.GetType().Name, right.GetType().Name),
            };
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.ContentSha256, right.ContentSha256);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                left.AuthoritativeDeclarationId,
                right.AuthoritativeDeclarationId);
            if (comparison != 0)
            {
                return comparison;
            }

            foreach (var (first, second) in new[]
            {
                (left.ObservationDeclarationSpan, right.ObservationDeclarationSpan),
                (left.RequestedDeclarationSpan, right.RequestedDeclarationSpan),
                (left.CanonicalDeclarationSpan, right.CanonicalDeclarationSpan),
                (left.OwnerSpan, right.OwnerSpan),
            })
            {
                comparison = CompareSpan(first, second);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (left.DocumentationSpan is null || right.DocumentationSpan is null)
            {
                return left.DocumentationSpan is null
                    ? right.DocumentationSpan is null ? 0 : -1
                    : 1;
            }

            return CompareSpan(left.DocumentationSpan.Value, right.DocumentationSpan.Value);
        }

        private static int CompareRepositorySource(
            CampaignPlanningRepositorySourceAuthority left,
            CampaignPlanningRepositorySourceAuthority right)
        {
            var comparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                left.PhysicalSourceCommitmentSha256,
                right.PhysicalSourceCommitmentSha256);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                left.ObservedSourceTextSha256,
                right.ObservedSourceTextSha256);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(EncodingId(left.Encoding), EncodingId(right.Encoding));
        }

        private static int CompareGeneratedSource(
            CampaignPlanningGeneratedSourceAuthority left,
            CampaignPlanningGeneratedSourceAuthority right)
        {
            var comparison = StringComparer.Ordinal.Compare(left.ProducerId, right.ProducerId);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.OutputId, right.OutputId);
        }

        private static int CompareSpan(Utf16Span left, Utf16Span right)
        {
            var comparison = left.Start.CompareTo(right.Start);
            return comparison != 0 ? comparison : left.End.CompareTo(right.End);
        }

        private static int CompareSymbols(
            ImmutableArray<SymbolRef> left,
            ImmutableArray<SymbolRef> right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = CompareSymbolRef(left[index], right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int CompareComponents(
            ImmutableArray<CampaignPlanningApplicableComponent> left,
            ImmutableArray<CampaignPlanningApplicableComponent> right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = StringComparer.Ordinal.Compare(
                    ClassificationVocabulary.GetId(left[index].Kind),
                    ClassificationVocabulary.GetId(right[index].Kind));
                if (comparison == 0)
                {
                    comparison = StringComparer.Ordinal.Compare(
                        left[index].Identity,
                        right[index].Identity);
                }
                if (comparison == 0)
                {
                    comparison = StringComparer.Ordinal.Compare(
                        left[index].Name ?? string.Empty,
                        right[index].Name ?? string.Empty);
                }
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int CompareCauses(
            ImmutableArray<CampaignPlanningViolationCause> left,
            ImmutableArray<CampaignPlanningViolationCause> right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = CompareSymbolRef(
                    left[index].ParentSymbolRef,
                    right[index].ParentSymbolRef);
                if (comparison == 0)
                {
                    comparison = StringComparer.Ordinal.Compare(
                        left[index].ComponentKind is { } leftKind
                            ? ClassificationVocabulary.GetId(leftKind)
                            : string.Empty,
                        right[index].ComponentKind is { } rightKind
                            ? ClassificationVocabulary.GetId(rightKind)
                            : string.Empty);
                }
                if (comparison == 0)
                {
                    comparison = StringComparer.Ordinal.Compare(
                        left[index].ComponentIdentity ?? string.Empty,
                        right[index].ComponentIdentity ?? string.Empty);
                }
                if (comparison == 0)
                {
                    comparison = StringComparer.Ordinal.Compare(
                        AuditVocabulary.GetId(left[index].Reason),
                        AuditVocabulary.GetId(right[index].Reason));
                }
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int CompareDisposition(
            CampaignPlanningDisposition left,
            CampaignPlanningDisposition right)
        {
            var comparison = StringComparer.Ordinal.Compare(
                CampaignPlanningVocabulary.GetId(left.Kind),
                CampaignPlanningVocabulary.GetId(right.Kind));
            if (comparison != 0)
            {
                return comparison;
            }

            for (var index = 0;
                 index < Math.Min(left.TerminalReasons.Length, right.TerminalReasons.Length);
                 index++)
            {
                comparison = StringComparer.Ordinal.Compare(
                    CampaignPlanningVocabulary.GetId(left.TerminalReasons[index]),
                    CampaignPlanningVocabulary.GetId(right.TerminalReasons[index]));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.TerminalReasons.Length.CompareTo(right.TerminalReasons.Length);
        }
    }
}
