using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public enum CampaignPlanningValidationCode
{
    InvalidRoot,
    InvalidBound,
    InvalidVocabulary,
    InvalidCommitment,
    TargetProfileMismatch,
    InvalidClassificationAuthority,
    InvalidObservationAuthority,
    InvalidAuditAuthority,
    InvalidOwnerAuthority,
    InvalidStyleAuthority,
    InvalidConfiguration,
    DuplicateWorkItemKey,
}

public sealed class CampaignPlanningValidationException : FormatException
{
    internal CampaignPlanningValidationException(
        CampaignPlanningValidationCode code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public CampaignPlanningValidationCode Code { get; }
}

public enum CampaignPlanningDispositionKind
{
    Executable,
    Terminal,
}

public enum CampaignPlanningEditCapability
{
    Insert,
    Replace,
}

public enum CampaignPlanningTerminalReason
{
    AmbiguousOwner,
    SharedOwner,
    MultiDeclarator,
    PrimaryConstructorAlias,
    PrimaryConstructor,
    NonRepositorySource,
    NonWritableSource,
    UnsupportedTargetKind,
    UnsupportedRemoval,
    UnsupportedBlockState,
}

public enum CampaignPlanningContentFamily
{
    ProposalContract,
    AgentProtocol,
    ContextSelectionPolicy,
    ToolPolicyAndRegistry,
    ProviderModelRequestProfile,
    RetryPolicy,
    M2ProjectionPolicy,
    ProductContractRevision,
    CostRatePolicy,
}

public static class CampaignPlanningVocabulary
{
    public const string PlanningContractRevision = "campaign-planning-v1";
    public const string SelectionPolicy = "campaign.selection.every-current-violation.v1";
    public const string OrderingPolicy = "campaign.order.complete-owner.ordinal.v1";

    public static string GetId(CampaignPlanningDispositionKind value) => value switch
    {
        CampaignPlanningDispositionKind.Executable => "campaign.work.executable",
        CampaignPlanningDispositionKind.Terminal => "campaign.work.terminal",
        _ => throw Unknown(value),
    };

    public static string GetId(CampaignPlanningEditCapability value) => value switch
    {
        CampaignPlanningEditCapability.Insert => "campaign.edit.insert",
        CampaignPlanningEditCapability.Replace => "campaign.edit.replace",
        _ => throw Unknown(value),
    };

    public static string GetId(CampaignPlanningTerminalReason value) => value switch
    {
        CampaignPlanningTerminalReason.AmbiguousOwner => "campaign.terminal.ambiguous-owner",
        CampaignPlanningTerminalReason.SharedOwner => "campaign.terminal.shared-owner",
        CampaignPlanningTerminalReason.MultiDeclarator => "campaign.terminal.multi-declarator",
        CampaignPlanningTerminalReason.PrimaryConstructorAlias => "campaign.terminal.primary-constructor-alias",
        CampaignPlanningTerminalReason.PrimaryConstructor => "campaign.terminal.primary-constructor",
        CampaignPlanningTerminalReason.NonRepositorySource => "campaign.terminal.non-repository-source",
        CampaignPlanningTerminalReason.NonWritableSource => "campaign.terminal.non-writable-source",
        CampaignPlanningTerminalReason.UnsupportedTargetKind => "campaign.terminal.unsupported-target-kind",
        CampaignPlanningTerminalReason.UnsupportedRemoval => "campaign.terminal.unsupported-removal",
        CampaignPlanningTerminalReason.UnsupportedBlockState => "campaign.terminal.unsupported-block-state",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, "The value is outside the closed campaign-planning vocabulary.");
}

public sealed record CampaignPlanningContentAuthority
{
    private CampaignPlanningContentAuthority(
        CampaignPlanningContentFamily family,
        string id,
        string contentSha256)
    {
        Family = family;
        Id = id;
        ContentSha256 = contentSha256;
    }

    public CampaignPlanningContentFamily Family { get; }

    public string Id { get; }

    public string ContentSha256 { get; }

    public static CampaignPlanningContentAuthority CreateValidatedJsonProjection(
        CampaignPlanningContentFamily family,
        string id,
        JsonElement validatedProjection)
    {
        if (!Enum.IsDefined(family)
            || string.IsNullOrEmpty(id)
            || validatedProjection.ValueKind != JsonValueKind.Object)
        {
            throw new CampaignPlanningValidationException(
                CampaignPlanningValidationCode.InvalidConfiguration,
                "Configuration authority requires a closed family, identifier, and JSON object projection.");
        }

        var canonicalProjection = CampaignPlanningProjectionCanonicalizer.Canonicalize(validatedProjection);
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign-configuration-authority/v1");
        writer.Add("family", GetContentFamilyId(family));
        writer.Add("projection", Encoding.UTF8.GetString(canonicalProjection));
        return new CampaignPlanningContentAuthority(
            family,
            id,
            writer.Complete());
    }

    internal static string GetContentFamilyId(CampaignPlanningContentFamily family) => family switch
    {
        CampaignPlanningContentFamily.ProposalContract => "configuration.proposal-contract",
        CampaignPlanningContentFamily.AgentProtocol => "configuration.agent-protocol",
        CampaignPlanningContentFamily.ContextSelectionPolicy => "configuration.context-selection",
        CampaignPlanningContentFamily.ToolPolicyAndRegistry => "configuration.tool-policy-registry",
        CampaignPlanningContentFamily.ProviderModelRequestProfile => "configuration.provider-model-request",
        CampaignPlanningContentFamily.RetryPolicy => "configuration.retry-policy",
        CampaignPlanningContentFamily.M2ProjectionPolicy => "configuration.m2-projection",
        CampaignPlanningContentFamily.ProductContractRevision => "configuration.product-contract-revision",
        CampaignPlanningContentFamily.CostRatePolicy => "configuration.cost-rate-policy",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown configuration family."),
    };
}

public sealed record CampaignPlanningSnapshot
{
    public CampaignPlanningSnapshot(
        string campaignLineage,
        string opaqueSnapshotBinding,
        string repositoryCommitmentSha256,
        string inputCommitmentSha256,
        string policyAuthorityCommitmentSha256,
        TargetProfile targetProfile)
    {
        CampaignLineage = campaignLineage;
        OpaqueSnapshotBinding = opaqueSnapshotBinding;
        RepositoryCommitmentSha256 = repositoryCommitmentSha256;
        InputCommitmentSha256 = inputCommitmentSha256;
        PolicyAuthorityCommitmentSha256 = policyAuthorityCommitmentSha256;
        TargetProfile = targetProfile;
    }

    public string CampaignLineage { get; init; }

    public string OpaqueSnapshotBinding { get; init; }

    public string RepositoryCommitmentSha256 { get; init; }

    public string InputCommitmentSha256 { get; init; }

    public string PolicyAuthorityCommitmentSha256 { get; init; }

    public TargetProfile TargetProfile { get; init; }
}

public sealed record CampaignPlanningBudgetPolicy
{
    public CampaignPlanningBudgetPolicy(
        int maximumBlocks,
        int maximumChangedFiles,
        long maximumPatchBytes,
        int maximumProviderRequests,
        int maximumAttemptsPerTarget,
        long maximumInputTokens,
        long maximumUncachedInputTokens,
        long maximumOutputTokens,
        long maximumCostMicrounits,
        long maximumElapsedMilliseconds,
        int maximumCandidatesPerBlock,
        bool costEnforced,
        string? costCurrency,
        CampaignPlanningContentAuthority? costRatePolicy)
    {
        MaximumBlocks = maximumBlocks;
        MaximumChangedFiles = maximumChangedFiles;
        MaximumPatchBytes = maximumPatchBytes;
        MaximumProviderRequests = maximumProviderRequests;
        MaximumAttemptsPerTarget = maximumAttemptsPerTarget;
        MaximumInputTokens = maximumInputTokens;
        MaximumUncachedInputTokens = maximumUncachedInputTokens;
        MaximumOutputTokens = maximumOutputTokens;
        MaximumCostMicrounits = maximumCostMicrounits;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
        MaximumCandidatesPerBlock = maximumCandidatesPerBlock;
        CostEnforced = costEnforced;
        CostCurrency = costCurrency;
        CostRatePolicy = costRatePolicy;
    }

    public int MaximumBlocks { get; }
    public int MaximumChangedFiles { get; }
    public long MaximumPatchBytes { get; }
    public int MaximumProviderRequests { get; }
    public int MaximumAttemptsPerTarget { get; }
    public long MaximumInputTokens { get; }
    public long MaximumUncachedInputTokens { get; }
    public long MaximumOutputTokens { get; }
    public long MaximumCostMicrounits { get; }
    public long MaximumElapsedMilliseconds { get; }
    public int MaximumCandidatesPerBlock { get; }
    public bool CostEnforced { get; }
    public string? CostCurrency { get; }
    public CampaignPlanningContentAuthority? CostRatePolicy { get; }
}

public sealed record CampaignPlanningExecutionPolicy
{
    public CampaignPlanningExecutionPolicy(
        DocumentationScribeRunLimits scribeRunLimits,
        CampaignPlanningBudgetPolicy campaignBudget,
        CampaignPlanningContentAuthority proposalContract,
        CampaignPlanningContentAuthority agentProtocol,
        CampaignPlanningContentAuthority contextSelectionPolicy,
        CampaignPlanningContentAuthority toolPolicyAndRegistry,
        CampaignPlanningContentAuthority providerModelRequestProfile,
        CampaignPlanningContentAuthority retryPolicy,
        CampaignPlanningContentAuthority m2ProjectionPolicy,
        CampaignPlanningContentAuthority productContractRevision)
    {
        ScribeRunLimits = scribeRunLimits;
        CampaignBudget = campaignBudget;
        ProposalContract = proposalContract;
        AgentProtocol = agentProtocol;
        ContextSelectionPolicy = contextSelectionPolicy;
        ToolPolicyAndRegistry = toolPolicyAndRegistry;
        ProviderModelRequestProfile = providerModelRequestProfile;
        RetryPolicy = retryPolicy;
        M2ProjectionPolicy = m2ProjectionPolicy;
        ProductContractRevision = productContractRevision;
    }

    public DocumentationScribeRunLimits ScribeRunLimits { get; init; }
    public CampaignPlanningBudgetPolicy CampaignBudget { get; init; }
    public CampaignPlanningContentAuthority ProposalContract { get; init; }
    public CampaignPlanningContentAuthority AgentProtocol { get; init; }
    public CampaignPlanningContentAuthority ContextSelectionPolicy { get; init; }
    public CampaignPlanningContentAuthority ToolPolicyAndRegistry { get; init; }
    public CampaignPlanningContentAuthority ProviderModelRequestProfile { get; init; }
    public CampaignPlanningContentAuthority RetryPolicy { get; init; }
    public CampaignPlanningContentAuthority M2ProjectionPolicy { get; init; }
    public CampaignPlanningContentAuthority ProductContractRevision { get; init; }
}

public abstract record CampaignPlanningSourceAuthority
{
    private protected CampaignPlanningSourceAuthority(
        DocumentationPatchSourceKind kind,
        string authoritativeDeclarationId,
        string contentSha256,
        Utf16Span observationDeclarationSpan,
        Utf16Span requestedDeclarationSpan,
        Utf16Span canonicalDeclarationSpan,
        Utf16Span ownerSpan,
        Utf16Span? documentationSpan,
        DocumentationBlockState blockState,
        bool writable)
    {
        Kind = kind;
        AuthoritativeDeclarationId = authoritativeDeclarationId;
        ContentSha256 = contentSha256;
        ObservationDeclarationSpan = observationDeclarationSpan;
        RequestedDeclarationSpan = requestedDeclarationSpan;
        CanonicalDeclarationSpan = canonicalDeclarationSpan;
        OwnerSpan = ownerSpan;
        DocumentationSpan = documentationSpan;
        BlockState = blockState;
        Writable = writable;
    }

    public DocumentationPatchSourceKind Kind { get; }
    public string AuthoritativeDeclarationId { get; }
    public string ContentSha256 { get; }
    public Utf16Span ObservationDeclarationSpan { get; }
    public Utf16Span RequestedDeclarationSpan { get; }
    public Utf16Span CanonicalDeclarationSpan { get; }
    public Utf16Span OwnerSpan { get; }
    public Utf16Span? DocumentationSpan { get; }
    public DocumentationBlockState BlockState { get; }
    public bool Writable { get; }
}

public sealed record CampaignPlanningRepositorySourceAuthority
    : CampaignPlanningSourceAuthority
{
    public CampaignPlanningRepositorySourceAuthority(
        string path,
        string physicalSourceCommitmentSha256,
        string authoritativeDeclarationId,
        string exactFileSha256,
        DocumentationPatchRepositoryEncoding encoding,
        Utf16Span observationDeclarationSpan,
        Utf16Span requestedDeclarationSpan,
        Utf16Span canonicalDeclarationSpan,
        Utf16Span ownerSpan,
        Utf16Span? documentationSpan,
        DocumentationBlockState blockState,
        bool writable = true)
        : base(
            DocumentationPatchSourceKind.Repository,
            authoritativeDeclarationId,
            exactFileSha256,
            observationDeclarationSpan,
            requestedDeclarationSpan,
            canonicalDeclarationSpan,
            ownerSpan,
            documentationSpan,
            blockState,
            writable)
    {
        Path = path;
        PhysicalSourceCommitmentSha256 = physicalSourceCommitmentSha256;
        Encoding = encoding;
    }

    public string Path { get; }
    public string PhysicalSourceCommitmentSha256 { get; }
    public DocumentationPatchRepositoryEncoding Encoding { get; }
}

public sealed record CampaignPlanningGeneratedSourceAuthority
    : CampaignPlanningSourceAuthority
{
    public CampaignPlanningGeneratedSourceAuthority(
        DocumentationPatchSourceKind kind,
        string authoritativeDeclarationId,
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span observationDeclarationSpan,
        Utf16Span requestedDeclarationSpan,
        Utf16Span canonicalDeclarationSpan,
        Utf16Span ownerSpan,
        Utf16Span? documentationSpan,
        DocumentationBlockState blockState)
        : base(
            kind,
            authoritativeDeclarationId,
            sourceSha256,
            observationDeclarationSpan,
            requestedDeclarationSpan,
            canonicalDeclarationSpan,
            ownerSpan,
            documentationSpan,
            blockState,
            writable: false)
    {
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string ProducerId { get; }
    public string OutputId { get; }
}

public sealed record CampaignPlanningApplicableComponent
{
    public CampaignPlanningApplicableComponent(
        ComponentKind kind,
        string identity,
        string? name)
    {
        Kind = kind;
        Identity = identity;
        Name = name;
    }

    public ComponentKind Kind { get; }
    public string Identity { get; }
    public string? Name { get; }
}

public sealed record CampaignPlanningTargetAuthority
{
    public CampaignPlanningTargetAuthority(
        TargetClassification target,
        CampaignPlanningSourceAuthority source,
        ImmutableArray<CampaignPlanningApplicableComponent> applicableComponents,
        ImmutableArray<SymbolRef> ownerSymbolRefs,
        bool multiDeclarator,
        bool primaryConstructor,
        bool primaryConstructorAlias,
        DocumentationScribeStyleProfile? executableStyleProfile)
    {
        Target = target;
        Source = source;
        ApplicableComponents = applicableComponents;
        OwnerSymbolRefs = ownerSymbolRefs;
        MultiDeclarator = multiDeclarator;
        PrimaryConstructor = primaryConstructor;
        PrimaryConstructorAlias = primaryConstructorAlias;
        ExecutableStyleProfile = executableStyleProfile;
    }

    public TargetClassification Target { get; init; }
    public CampaignPlanningSourceAuthority Source { get; init; }
    public ImmutableArray<CampaignPlanningApplicableComponent> ApplicableComponents { get; init; }
    public ImmutableArray<SymbolRef> OwnerSymbolRefs { get; init; }
    public bool MultiDeclarator { get; init; }
    public bool PrimaryConstructor { get; init; }
    public bool PrimaryConstructorAlias { get; init; }
    public DocumentationScribeStyleProfile? ExecutableStyleProfile { get; init; }
}

public sealed record CampaignPlanningOwnerAuthority
{
    public CampaignPlanningOwnerAuthority(
        ImmutableArray<CampaignPlanningTargetAuthority> targets,
        bool ambiguousOwner = false)
    {
        Targets = targets;
        AmbiguousOwner = ambiguousOwner;
    }

    public ImmutableArray<CampaignPlanningTargetAuthority> Targets { get; init; }
    public bool AmbiguousOwner { get; init; }
}

public sealed record CampaignPlanningEvidenceAuthority
{
    public CampaignPlanningEvidenceAuthority(
        DocumentationObservation observation,
        BoundObservationEvidence binding)
    {
        Subject = observation.Subject;
        ObservationAuthorityCommitmentSha256 =
            CampaignPlanningObservationProjection.ComputeCommitment(observation);
        Binding = binding;
    }

    public DocumentationObservationSubject Subject { get; init; }
    public string ObservationAuthorityCommitmentSha256 { get; }
    public BoundObservationEvidence Binding { get; init; }
}

public sealed record CampaignPlanningOwnerAuthoritySet
{
    public CampaignPlanningOwnerAuthoritySet(
        ImmutableArray<CampaignPlanningOwnerAuthority> owners)
    {
        Owners = owners;
    }

    public ImmutableArray<CampaignPlanningOwnerAuthority> Owners { get; init; }
}

public sealed record CampaignPlanningInput
{
    public CampaignPlanningInput(
        CampaignPlanningSnapshot snapshot,
        CampaignPlanningExecutionPolicy executionPolicy,
        ClassificationSet classifications,
        DocumentationObservationSet observations,
        ImmutableArray<CampaignPlanningEvidenceAuthority> evidenceAuthority,
        AuditDocument auditDocument,
        CampaignPlanningOwnerAuthoritySet ownerAuthority)
    {
        Snapshot = snapshot;
        ExecutionPolicy = executionPolicy;
        Classifications = classifications;
        Observations = observations;
        EvidenceAuthority = evidenceAuthority;
        AuditDocument = auditDocument;
        OwnerAuthority = ownerAuthority;
    }

    public CampaignPlanningSnapshot Snapshot { get; init; }
    public CampaignPlanningExecutionPolicy ExecutionPolicy { get; init; }
    public ClassificationSet Classifications { get; init; }
    public DocumentationObservationSet Observations { get; init; }
    public ImmutableArray<CampaignPlanningEvidenceAuthority> EvidenceAuthority { get; init; }
    public AuditDocument AuditDocument { get; init; }
    public CampaignPlanningOwnerAuthoritySet OwnerAuthority { get; init; }
}

public sealed record CampaignPlanningViolationCause
{
    internal CampaignPlanningViolationCause(
        SymbolRef parentSymbolRef,
        ComponentKind? componentKind,
        string? componentIdentity,
        AuditReason reason,
        string auditRowSha256)
    {
        ParentSymbolRef = parentSymbolRef;
        ComponentKind = componentKind;
        ComponentIdentity = componentIdentity;
        Reason = reason;
        AuditRowSha256 = auditRowSha256;
    }

    public SymbolRef ParentSymbolRef { get; }
    public ComponentKind? ComponentKind { get; }
    public string? ComponentIdentity { get; }
    public AuditReason Reason { get; }
    public string AuditRowSha256 { get; }
}

public sealed record CampaignPlanningTargetFact
{
    internal CampaignPlanningTargetFact(
        SymbolRef symbolRef,
        PrimarySymbolKind primaryKind,
        ClassificationOrigin origin,
        CampaignPlanningSourceAuthority source,
        string authoritativeDeclarationId,
        ImmutableArray<CampaignPlanningApplicableComponent> applicableComponents,
        ImmutableArray<SymbolRef> ownerSymbolRefs,
        AuditOutcome auditOutcome,
        AuditReason auditReason,
        string auditRowSha256,
        bool m3Eligible,
        DocumentationScribeStyleProfile? styleProfile)
    {
        SymbolRef = symbolRef;
        PrimaryKind = primaryKind;
        Origin = origin;
        Source = source;
        AuthoritativeDeclarationId = authoritativeDeclarationId;
        ApplicableComponents = applicableComponents;
        OwnerSymbolRefs = ownerSymbolRefs;
        AuditOutcome = auditOutcome;
        AuditReason = auditReason;
        AuditRowSha256 = auditRowSha256;
        M3Eligible = m3Eligible;
        StyleProfile = styleProfile;
    }

    public SymbolRef SymbolRef { get; }
    public PrimarySymbolKind PrimaryKind { get; }
    public ClassificationOrigin Origin { get; }
    public CampaignPlanningSourceAuthority Source { get; }
    public string AuthoritativeDeclarationId { get; }
    public ImmutableArray<CampaignPlanningApplicableComponent> ApplicableComponents { get; }
    public ImmutableArray<SymbolRef> OwnerSymbolRefs { get; }
    public AuditOutcome AuditOutcome { get; }
    public AuditReason AuditReason { get; }
    public string AuditRowSha256 { get; }
    public bool M3Eligible { get; }
    public DocumentationScribeStyleProfile? StyleProfile { get; }
}

public sealed record CampaignPlanningDisposition
{
    internal CampaignPlanningDisposition(
        CampaignPlanningDispositionKind kind,
        CampaignPlanningEditCapability? editCapability,
        CampaignPlanningTerminalReason? primaryTerminalReason,
        ImmutableArray<CampaignPlanningTerminalReason> terminalReasons)
    {
        Kind = kind;
        EditCapability = editCapability;
        PrimaryTerminalReason = primaryTerminalReason;
        TerminalReasons = terminalReasons;
    }

    public CampaignPlanningDispositionKind Kind { get; }
    public CampaignPlanningEditCapability? EditCapability { get; }
    public CampaignPlanningTerminalReason? PrimaryTerminalReason { get; }
    public ImmutableArray<CampaignPlanningTerminalReason> TerminalReasons { get; }
}

public sealed record CampaignPlanningWorkItem
{
    internal CampaignPlanningWorkItem(
        string workItemKey,
        string ownerEquivalenceRef,
        ImmutableArray<CampaignPlanningTargetFact> targets,
        ImmutableArray<CampaignPlanningViolationCause> violationCauses,
        CampaignPlanningDisposition disposition)
    {
        WorkItemKey = workItemKey;
        OwnerEquivalenceRef = ownerEquivalenceRef;
        Targets = targets;
        ViolationCauses = violationCauses;
        Disposition = disposition;
    }

    public string WorkItemKey { get; }
    public string OwnerEquivalenceRef { get; }
    public ImmutableArray<CampaignPlanningTargetFact> Targets { get; }
    public ImmutableArray<CampaignPlanningViolationCause> ViolationCauses { get; }
    public CampaignPlanningDisposition Disposition { get; }
}

public sealed record CampaignPlanningSummary(
    int TotalWorkItems,
    int ExecutableWorkItems,
    int TerminalWorkItems,
    int RepositoryBackedWorkItems,
    int GeneratedOrNonWritableWorkItems,
    ImmutableArray<CampaignPlanningTerminalReasonCount> TerminalReasonCounts);

public sealed record CampaignPlanningTerminalReasonCount(
    CampaignPlanningTerminalReason Reason,
    int Count);

public sealed record CampaignWorkPlan
{
    internal CampaignWorkPlan(
        string campaignLineage,
        string opaqueSnapshotBinding,
        string auditDocumentSha256,
        string executionCommitment,
        TargetProfile targetProfile,
        ImmutableArray<CampaignPlanningWorkItem> workItems,
        CampaignPlanningSummary summary)
    {
        CampaignLineage = campaignLineage;
        OpaqueSnapshotBinding = opaqueSnapshotBinding;
        AuditDocumentSha256 = auditDocumentSha256;
        ExecutionCommitment = executionCommitment;
        TargetProfile = targetProfile;
        WorkItems = workItems;
        Summary = summary;
    }

    public string CampaignLineage { get; }
    public string OpaqueSnapshotBinding { get; }
    public string AuditDocumentSha256 { get; }
    public string ExecutionCommitment { get; }
    public TargetProfile TargetProfile { get; }
    public ImmutableArray<CampaignPlanningWorkItem> WorkItems { get; }
    public CampaignPlanningSummary Summary { get; }
}
