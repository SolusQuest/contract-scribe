using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class CampaignPlanner
{
    private const int MaximumOwners = 16_384;
    private const int MaximumTargets = 65_536;
    private const int MaximumIdentifierScalars = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CampaignWorkPlan Plan(CampaignPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRoot(input);

        var auditBytes = AuditJson.Write(input.AuditDocument);
        var auditSha256 = Sha256(auditBytes);
        var auditRows = ReadAuditRows(input.AuditDocument);
        var observations = ValidateObservations(input.Classifications, input.Observations);
        ValidateAuditAuthority(input.Classifications, observations, auditRows);
        _ = ValidateOwnerAuthority(
            input.Classifications,
            observations,
            input.OwnerAuthority);

        var violationRows = auditRows.Values
            .Where(row => row.Outcome == AuditOutcome.Violation)
            .OrderBy(row => row.SubjectKey, StringComparer.Ordinal)
            .ToImmutableArray();
        var selectedOwners = new List<PendingWorkItem>();
        foreach (var owner in input.OwnerAuthority.Owners)
        {
            var targetRefs = owner.Targets
                .Select(target => target.Target.SymbolRef)
                .ToHashSet();
            var causes = violationRows
                .Where(row => targetRefs.Contains(row.ParentSymbolRef))
                .Select(row => new CampaignPlanningViolationCause(
                    row.ParentSymbolRef,
                    row.ComponentKind,
                    row.ComponentIdentity,
                    row.Reason,
                    row.RowSha256))
                .ToImmutableArray();
            if (causes.IsEmpty)
            {
                continue;
            }

            var pending = BuildPendingItem(owner, auditRows, causes);
            selectedOwners.Add(pending);
        }

        Require(
            violationRows.All(row => selectedOwners.Count(owner =>
                owner.Targets.Any(target => target.SymbolRef == row.ParentSymbolRef)) == 1),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Every violation subject must resolve to exactly one owner authority row.");

        var ordered = selectedOwners
            .OrderBy(item => item.OrderKey, StringComparer.Ordinal)
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
        Require(input.AuditDocument is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Audit authority is required.");
        Require(input.OwnerAuthority is not null, CampaignPlanningValidationCode.InvalidRoot,
            "Owner authority is required.");

        var snapshot = input.Snapshot
            ?? throw Failure(CampaignPlanningValidationCode.InvalidRoot,
                "Campaign snapshot authority is required.");
        RequireIdentifier(snapshot.CampaignLineage, nameof(snapshot.CampaignLineage));
        RequireIdentifier(snapshot.OpaqueSnapshotBinding, nameof(snapshot.OpaqueSnapshotBinding));
        RequireSha256(snapshot.RepositoryCommitmentSha256, nameof(snapshot.RepositoryCommitmentSha256));
        RequireSha256(snapshot.InputCommitmentSha256, nameof(snapshot.InputCommitmentSha256));
        RequireSha256(snapshot.PolicyAuthorityCommitmentSha256, nameof(snapshot.PolicyAuthorityCommitmentSha256));
        Require(
            Enum.IsDefined(snapshot.TargetProfile)
            && input.Classifications!.TargetProfile == snapshot.TargetProfile
            && input.AuditDocument!.TargetProfile == snapshot.TargetProfile,
            CampaignPlanningValidationCode.TargetProfileMismatch,
            "Snapshot, Classification, and Audit target profiles must match.");

        ValidateExecutionPolicy(input.ExecutionPolicy!);
        Require(
            !input.OwnerAuthority!.Owners.IsDefault
            && input.OwnerAuthority.Owners.Length <= MaximumOwners
            && input.OwnerAuthority.Owners.All(owner => owner is not null),
            CampaignPlanningValidationCode.InvalidBound,
            "Owner authority must be initialized and bounded.");
    }

    private static void ValidateExecutionPolicy(CampaignPlanningExecutionPolicy policy)
    {
        Require(policy.ScribeRunLimits is not null && policy.CampaignBudget is not null,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Scribe and campaign limits are required.");
        foreach (var authority in new[]
        {
            policy.ProposalContract,
            policy.AgentProtocol,
            policy.ContextSelectionPolicy,
            policy.ToolPolicyAndRegistry,
            policy.ProviderModelRequestProfile,
            policy.RetryPolicy,
            policy.M2ProjectionPolicy,
            policy.ProductContractRevision,
        })
        {
            Require(authority is not null, CampaignPlanningValidationCode.InvalidConfiguration,
                "Every correctness-bearing configuration family requires content authority.");
            RequireIdentifier(authority!.Id, nameof(authority.Id));
            RequireSha256(authority.ContentSha256, nameof(authority.ContentSha256));
        }

        var limits = policy.ScribeRunLimits!;
        Require(
            limits.MaximumContextReferences >= 0
            && limits.MaximumContextUtf8Bytes >= 0
            && limits.MaximumEvidenceReferences >= 0
            && limits.MaximumEvidenceUtf8Bytes >= 0
            && limits.MaximumProviderRequests > 0
            && limits.MaximumToolRounds >= 0
            && limits.MaximumToolCalls >= 0
            && limits.MaximumAttempts > 0
            && limits.MaximumInputTokens > 0
            && limits.MaximumUncachedInputTokens >= 0
            && limits.MaximumOutputTokens > 0
            && limits.MaximumCostMicrounits >= 0
            && limits.MaximumElapsedMilliseconds > 0,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Scribe limits must use non-negative bounded values and positive active ceilings.");

        var budget = policy.CampaignBudget!;
        Require(
            budget.MaximumBlocks > 0
            && budget.MaximumChangedFiles > 0
            && budget.MaximumPatchBytes > 0
            && budget.MaximumProviderRequests > 0
            && budget.MaximumAttemptsPerTarget > 0
            && budget.MaximumInputTokens > 0
            && budget.MaximumUncachedInputTokens >= 0
            && budget.MaximumOutputTokens > 0
            && budget.MaximumCostMicrounits >= 0
            && budget.MaximumElapsedMilliseconds > 0
            && budget.MaximumCandidatesPerBlock > 0,
            CampaignPlanningValidationCode.InvalidConfiguration,
            "Campaign budgets must use non-negative bounded values and positive active ceilings.");
        if (budget.CostEnforced)
        {
            RequireIdentifier(budget.CostCurrency!, nameof(budget.CostCurrency));
            Require(budget.CostRatePolicy is not null,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Cost enforcement requires currency and rate-policy content authority.");
            RequireIdentifier(budget.CostRatePolicy!.Id, nameof(budget.CostRatePolicy.Id));
            RequireSha256(budget.CostRatePolicy.ContentSha256, nameof(budget.CostRatePolicy.ContentSha256));
        }
        else
        {
            Require(budget.CostCurrency is null && budget.CostRatePolicy is null,
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Disabled cost enforcement cannot carry contradictory cost authority.");
        }
    }

    private static Dictionary<string, DocumentationObservation> ValidateObservations(
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
        foreach (var observation in observations.Observations)
        {
            Require(observation is not null && observation.Subject is not null,
                CampaignPlanningValidationCode.InvalidObservationAuthority,
                "Observation authority contains a null record.");
            var actualObservation = observation!;
            var key = ObservationKey(actualObservation.Subject!);
            Require(expected.Contains(key) && result.TryAdd(key, actualObservation),
                CampaignPlanningValidationCode.InvalidObservationAuthority,
                "Observation authority must cover every and only supported classification once.");
        }

        Require(result.Count == expected.Count,
            CampaignPlanningValidationCode.InvalidObservationAuthority,
            "Observation authority must cover every and only supported classification once.");
        return result;
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
            Require(result.TryAdd(key, new AuditRow(
                    key,
                    parent,
                    componentKind,
                    componentIdentity,
                    outcome,
                    reason,
                    Sha256(canonicalRow),
                    classification.Clone(),
                    row.GetProperty("documentationObservation").GetString())),
                CampaignPlanningValidationCode.InvalidAuditAuthority,
                "Audit authority contains a duplicate classification row.");
        }

        return result;
    }

    private static void ValidateAuditAuthority(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
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
                        && ObservationId(observation.Value) == row.ObservationId,
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
                        && ObservationId(observation.Value) == row.ObservationId,
                    CampaignPlanningValidationCode.InvalidAuditAuthority,
                    "Audit component observation does not match current observation authority.");
            }
        }
    }

    private static Dictionary<SymbolRef, ValidatedTargetAuthority> ValidateOwnerAuthority(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
        CampaignPlanningOwnerAuthoritySet authoritySet)
    {
        var supportedTargets = classifications.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .ToDictionary(target => target.SymbolRef);
        var result = new Dictionary<SymbolRef, ValidatedTargetAuthority>();
        var ownerRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in authoritySet.Owners)
        {
            Require(owner is not null, CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Owner authority cannot contain null records.");
            var actualOwner = owner!;
            RequireIdentifier(actualOwner.OwnerEquivalenceRef, nameof(actualOwner.OwnerEquivalenceRef));
            Require(ownerRefs.Add(actualOwner.OwnerEquivalenceRef)
                    && !actualOwner.Targets.IsDefault
                    && !actualOwner.Targets.IsEmpty
                    && actualOwner.Targets.Length <= MaximumTargets,
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Owner authority rows require a unique bounded equivalence reference and targets.");
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
                        && result.TryAdd(symbol, new ValidatedTargetAuthority(actualOwner, actualTargetAuthority)),
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Owner authority must cover every supported target exactly once.");
                ValidateSourceAuthority(actualTargetAuthority.Source!);
                ValidateOwnerSymbols(actualTargetAuthority);
                ValidateComponents(classifications, observations, actualTargetAuthority);
                ValidateSourceCorrelation(observations[TargetKey(symbol)], actualTargetAuthority.Source!);
                if (actualTargetAuthority.ExecutableStyleProfile is not null)
                {
                    ValidateStyleProfile(
                        actualTargetAuthority.ExecutableStyleProfile,
                        actualTargetAuthority.ApplicableComponents);
                }
            }
        }

        Require(result.Count == supportedTargets.Count,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner authority must cover every supported target exactly once.");
        return result;
    }

    private static void ValidateSourceAuthority(CampaignPlanningSourceAuthority source)
    {
        Require(Enum.IsDefined(source.Kind)
                && Enum.IsDefined(source.BlockState)
                && ValidSpan(source.RequestedDeclarationSpan)
                && ValidSpan(source.CanonicalDeclarationSpan)
                && ValidSpan(source.OwnerSpan)
                && (source.DocumentationSpan is null || ValidSpan(source.DocumentationSpan.Value)),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Source authority uses an invalid vocabulary value or span.");
        RequireSha256(source.ContentSha256, nameof(source.ContentSha256));
        switch (source)
        {
            case CampaignPlanningRepositorySourceAuthority repository:
                RequireCanonicalPath(repository.Path);
                Require(Enum.IsDefined(repository.Encoding),
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Repository source encoding is outside the closed vocabulary.");
                break;
            case CampaignPlanningGeneratedSourceAuthority generated:
                Require(generated.Kind is DocumentationPatchSourceKind.SourceGenerator
                        or DocumentationPatchSourceKind.ToolGenerated,
                    CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Generated source authority requires a generated source kind.");
                RequireIdentifier(generated.ProducerId, nameof(generated.ProducerId));
                RequireIdentifier(generated.OutputId, nameof(generated.OutputId));
                break;
            default:
                throw Failure(CampaignPlanningValidationCode.InvalidOwnerAuthority,
                    "Unknown source authority type.");
        }
    }

    private static void ValidateOwnerSymbols(CampaignPlanningTargetAuthority authority)
    {
        Require(!authority.OwnerSymbolRefs.IsDefault && !authority.OwnerSymbolRefs.IsEmpty,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner SymbolRefs must be initialized and non-empty.");
        var ordered = authority.OwnerSymbolRefs
            .OrderBy(SymbolKey, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(authority.OwnerSymbolRefs.SequenceEqual(ordered)
                && ordered.Distinct().Count() == ordered.Length
                && ordered.Contains(authority.Target.SymbolRef),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Owner SymbolRefs must be unique, canonical, and contain the target SymbolRef.");
    }

    private static void ValidateComponents(
        ClassificationSet classifications,
        IReadOnlyDictionary<string, DocumentationObservation> observations,
        CampaignPlanningTargetAuthority authority)
    {
        Require(!authority.ApplicableComponents.IsDefault,
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Applicable components must be initialized.");
        var expected = classifications.Components
            .Where(component => component.ParentSymbolRef == authority.Target.SymbolRef
                && component.SupportStatus == SupportStatus.Supported
                && IsApplicableComponent(component.ComponentKind))
            .OrderBy(component => ComponentRank(component.ComponentKind))
            .ThenBy(component => component.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
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
            var localNames = observation.Declarations
                .Select(declaration => declaration.ComponentLocalName)
                .Where(name => name is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Require(actualSupplied.Name is null
                    ? localNames.Length == 0
                    : localNames.Length == 1 && localNames[0] == actualSupplied.Name,
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Applicable-component names must match current observation authority.");
        }
    }

    private static void ValidateSourceCorrelation(
        DocumentationObservation observation,
        CampaignPlanningSourceAuthority source)
    {
        Require(observation.Declarations.Any(declaration =>
            SourceMatches(declaration.Source, source)
            && declaration.BlockState == source.BlockState),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Exact source authority must correlate with current observation source and block facts.");
    }

    private static bool SourceMatches(
        DocumentationSourceIdentity observed,
        CampaignPlanningSourceAuthority supplied) =>
        (observed, supplied) switch
        {
            (RepositoryDocumentationSourceIdentity left,
                CampaignPlanningRepositorySourceAuthority right) =>
                left.Path == right.Path,
            (GeneratedDocumentationSourceIdentity left,
                CampaignPlanningGeneratedSourceAuthority right) =>
                MapSourceKind(left.Kind) == right.Kind
                && left.ProducerId == right.ProducerId
                && left.OutputId == right.OutputId
                && left.SourceSha256 == right.ContentSha256,
            _ => false,
        };

    private static PendingWorkItem BuildPendingItem(
        CampaignPlanningOwnerAuthority owner,
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
                var eligible = owner.Targets.Length == 1
                    && !owner.AmbiguousOwner
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
                return new CampaignPlanningTargetFact(
                    authority.Target.SymbolRef,
                    authority.Target.PrimaryKind,
                    authority.Target.Origin,
                    authority.Source,
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
            disposition = new CampaignPlanningDisposition(
                CampaignPlanningDispositionKind.Terminal,
                null,
                orderedReasons[0],
                orderedReasons);
        }

        var pending = new PendingWorkItem(
            owner.OwnerEquivalenceRef,
            targetFacts,
            causes,
            disposition,
            string.Empty);
        return pending with { OrderKey = BuildOrderKey(pending) };
    }

    private static void ValidateStyleProfile(
        DocumentationScribeStyleProfile profile,
        ImmutableArray<CampaignPlanningApplicableComponent> components)
    {
        RequireIdentifier(profile.StyleProfileId, nameof(profile.StyleProfileId));
        RequireIdentifier(profile.OutputLanguageId, nameof(profile.OutputLanguageId));
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
        writer.Add("source.sha256", source.ContentSha256);
        writer.Add("source.writable", source.Writable);
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

    private static string BuildOrderKey(PendingWorkItem item)
    {
        var builder = new StringBuilder();
        AppendKey(builder, item.OwnerEquivalenceRef);
        foreach (var target in item.Targets)
        {
            AppendKey(builder, SourceKindId(target.Source.Kind));
            switch (target.Source)
            {
                case CampaignPlanningRepositorySourceAuthority repository:
                    AppendKey(builder, repository.Path);
                    AppendKey(builder, EncodingId(repository.Encoding));
                    break;
                case CampaignPlanningGeneratedSourceAuthority generated:
                    AppendKey(builder, generated.ProducerId);
                    AppendKey(builder, generated.OutputId);
                    break;
            }

            AppendKey(builder, target.Source.ContentSha256);
            AppendKey(builder, target.Source.OwnerSpan.Start);
            AppendKey(builder, target.Source.OwnerSpan.End);
            AppendKey(builder, SymbolKey(target.SymbolRef));
            AppendKey(builder, ClassificationVocabulary.GetId(target.PrimaryKind));
            foreach (var symbol in target.OwnerSymbolRefs)
            {
                AppendKey(builder, SymbolKey(symbol));
            }

            foreach (var component in target.ApplicableComponents)
            {
                AppendKey(builder, ClassificationVocabulary.GetId(component.Kind));
                AppendKey(builder, component.Identity);
                AppendKey(builder, component.Name ?? string.Empty);
            }

            AppendKey(builder, target.AuditRowSha256);
        }

        foreach (var cause in item.Causes)
        {
            AppendKey(builder, SymbolKey(cause.ParentSymbolRef));
            AppendKey(builder, cause.ComponentKind is null
                ? string.Empty
                : ClassificationVocabulary.GetId(cause.ComponentKind.Value));
            AppendKey(builder, cause.ComponentIdentity ?? string.Empty);
            AppendKey(builder, AuditVocabulary.GetId(cause.Reason));
        }

        foreach (var reason in item.Disposition.TerminalReasons)
        {
            AppendKey(builder, CampaignPlanningVocabulary.GetId(reason));
        }

        return builder.ToString();
    }

    private static void AppendKey(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    private static void AppendKey(StringBuilder builder, int value) =>
        builder.Append(value.ToString("D10", System.Globalization.CultureInfo.InvariantCulture));

    private static bool TargetClassificationMatches(TargetClassification target, JsonElement value)
    {
        var symbol = ReadSymbolRef(value.GetProperty("symbolRef"));
        return symbol == target.SymbolRef
            && value.GetProperty("primaryKind").GetString() == ClassificationVocabulary.GetId(target.PrimaryKind)
            && value.GetProperty("origin").GetString() == ClassificationVocabulary.GetId(target.Origin)
            && value.GetProperty("supportStatus").GetString() == ClassificationVocabulary.GetId(target.SupportStatus);
    }

    private static bool ComponentClassificationMatches(ComponentClassification component, JsonElement value)
    {
        var parent = ReadSymbolRef(value.GetProperty("parentSymbolRef"));
        return parent == component.ParentSymbolRef
            && value.GetProperty("componentKind").GetString() == ClassificationVocabulary.GetId(component.ComponentKind)
            && value.GetProperty("identity").GetString() == component.Identity
            && value.GetProperty("origin").GetString() == ClassificationVocabulary.GetId(component.Origin)
            && value.GetProperty("supportStatus").GetString() == ClassificationVocabulary.GetId(component.SupportStatus);
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
        string? ObservationId);

    private sealed record ValidatedTargetAuthority(
        CampaignPlanningOwnerAuthority Owner,
        CampaignPlanningTargetAuthority Target);

    private sealed record PendingWorkItem(
        string OwnerEquivalenceRef,
        ImmutableArray<CampaignPlanningTargetFact> Targets,
        ImmutableArray<CampaignPlanningViolationCause> Causes,
        CampaignPlanningDisposition Disposition,
        string OrderKey);
}
