using System.Collections.Immutable;

namespace ContractScribe.Core;

public static class CampaignStateContract
{
    public const int Version = 1;
    public const int MaximumArtifactUtf8Bytes = 4_194_304;
    public const int MaximumJsonDepth = 96;
    public const int MaximumWorkItems = 4_096;
    public const int MaximumActivePatchBlocks = 512;
    public const int MaximumChangedFiles = 512;
    public const int MaximumEvidenceReferences = 4_096;
    public const int MaximumEvidenceReferencesPerBlock = 64;
    public const int MaximumDiagnostics = 128;
    public const int MaximumIdentifierScalars = 512;
    public const int MaximumPathScalars = 512;
    public const long MaximumObservation = 1_000_000_000_000_000;
    public const long MaximumCampaignInputTokens = 1_000_000_000_000;
    public const long MaximumCampaignElapsedMilliseconds = 2_678_400_000;
    public const long MaximumPatchBytes = 1_099_511_627_776;
    public const string Revision = "campaign-state-v1";
}

public enum CampaignStateValidationCode
{
    DocumentTooLarge,
    BomNotAllowed,
    InvalidUtf8,
    InvalidJson,
    DuplicateProperty,
    UnsupportedVersion,
    InvalidShape,
    UnknownProperty,
    InvalidVocabulary,
    InvalidBound,
    InvalidOrder,
    InvalidReference,
    InvalidCorrelation,
    InvalidCanonicalBytes,
    InvalidConfiguration,
}

public sealed class CampaignStateValidationException : FormatException
{
    internal CampaignStateValidationException(
        CampaignStateValidationCode code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public CampaignStateValidationCode Code { get; }
}

public enum CampaignWorkStatus
{
    Planned,
    ProposalComplete,
    Accepted,
    Closed,
}

public enum CampaignWorkOutcomeStage
{
    Planning,
    Scribe,
    Patch,
}

public enum CampaignWorkOutcomeCode
{
    PlanningTerminal,
    InsufficientEvidence,
    UnsupportedDomain,
    ProviderFailure,
    ToolProtocolFailure,
    ValidationFailure,
    InternalFailure,
    CancelledByCaller,
    CancelledByShutdown,
    Timeout,
    BudgetExhausted,
    PatchRejected,
}

public enum CampaignProviderFinalDisposition
{
    Retryable,
    Terminal,
}

public enum CampaignCumulativeOutcomeKind
{
    Accepted,
    Rejected,
    Stale,
    HostFailure,
    Cancelled,
    Timeout,
}

public enum CampaignTerminalKind
{
    Complete,
    Exhausted,
    Cancelled,
    Timeout,
    Failed,
    Superseded,
}

public enum CampaignTerminalReason
{
    NoWork,
    AllWorkClosed,
    Budget,
    Caller,
    Deadline,
    Host,
    NewSnapshot,
}

public sealed record CampaignStateProductRevision(
    string Id,
    string ContentSha256);

public sealed record CampaignStateSnapshotAuthority(
    string OpaqueSnapshotBinding,
    string RepositoryCommitmentSha256,
    string InputCommitmentSha256,
    string InputIdentityCommitmentSha256,
    string PolicyAuthorityCommitmentSha256,
    TargetProfile TargetProfile,
    string ExecutionCommitmentSha256);

public sealed record CampaignStyleConfigurationAuthority(
    string Id,
    string ContentSha256);

public sealed record CampaignStateCampaignBudget(
    int MaximumBlocks,
    int MaximumChangedFiles,
    long MaximumPatchBytes,
    int MaximumProviderRequests,
    int MaximumAttemptsPerTarget,
    long MaximumInputTokens,
    long MaximumUncachedInputTokens,
    long MaximumOutputTokens,
    long MaximumCostMicrounits,
    long MaximumElapsedMilliseconds,
    int MaximumCandidatesPerBlock,
    bool CostEnforced,
    string? CostCurrency,
    string? CostRatePolicyId,
    string? CostRatePolicySha256);

public sealed record CampaignStateScribeLimits(
    int MaximumContextReferences,
    int MaximumContextUtf8Bytes,
    int MaximumEvidenceReferences,
    int MaximumEvidenceUtf8Bytes,
    int MaximumProviderRequests,
    int MaximumToolRounds,
    int MaximumToolCalls,
    int MaximumAttempts,
    int MaximumInputTokens,
    int MaximumUncachedInputTokens,
    int MaximumOutputTokens,
    long MaximumCostMicrounits,
    int MaximumElapsedMilliseconds);

public sealed record CampaignStateConfiguredCeilings(
    CampaignStateCampaignBudget CampaignBudget,
    CampaignStateScribeLimits ScribeRunLimits,
    CampaignStyleConfigurationAuthority StyleConfigurationAuthority,
    string CampaignConfigurationCommitmentSha256);

public sealed record CampaignChargeObservation(
    long? Observed,
    long ConservativeUnobserved,
    long TotalCharged);

public sealed record CampaignLineageCharges(
    long OuterInvocations,
    CampaignChargeObservation ProviderRequests,
    CampaignChargeObservation InputTokens,
    CampaignChargeObservation CachedInputTokens,
    CampaignChargeObservation UncachedInputTokens,
    CampaignChargeObservation OutputTokens,
    CampaignChargeObservation ReasoningTokens,
    CampaignChargeObservation CostMicrounits,
    CampaignChargeObservation ActiveElapsedMilliseconds,
    long PatchValidationInvocations);

public sealed record CampaignEvidenceProjection(
    string EvidenceReferenceId,
    EvidenceSubject Subject,
    EvidenceKind Kind,
    EvidenceRelation Relation,
    DocumentationScribeEvidenceAuthority Authority,
    EvidenceLocator Locator,
    string ContentSha256,
    int OriginalUtf8ByteCount,
    int IncludedUtf8ByteCount,
    bool IsTruncated,
    ImmutableArray<string> ClaimCategoryIds);

public sealed record CampaignTrustedProposal(
    string HistoricalScribeRequestSha256,
    DocumentationScribeAttemptId HistoricalAttemptId,
    string ProviderConfigurationId,
    string ModelConfigurationId,
    string ScribeProtocolId,
    DocumentationPatchBlockRequest PatchBlock,
    ImmutableArray<CampaignEvidenceProjection> Evidence,
    string StyleProfileCommitmentSha256,
    string ToolPolicyId,
    string ProposalCommitmentSha256);

public sealed record CampaignWorkClosedOutcome
{
    internal CampaignWorkClosedOutcome(
        CampaignWorkOutcomeStage stage,
        CampaignWorkOutcomeCode code,
        CampaignProviderFinalDisposition? providerDisposition,
        string? scribeRequestSha256,
        DocumentationScribeAttemptId? attemptId,
        string? patchRequestSha256,
        string? patchResultCommitmentSha256)
    {
        Stage = stage;
        Code = code;
        ProviderDisposition = providerDisposition;
        ScribeRequestSha256 = scribeRequestSha256;
        AttemptId = attemptId;
        PatchRequestSha256 = patchRequestSha256;
        PatchResultCommitmentSha256 = patchResultCommitmentSha256;
    }

    public CampaignWorkOutcomeStage Stage { get; }
    public CampaignWorkOutcomeCode Code { get; }
    public CampaignProviderFinalDisposition? ProviderDisposition { get; }
    public string? ScribeRequestSha256 { get; }
    public DocumentationScribeAttemptId? AttemptId { get; }
    public string? PatchRequestSha256 { get; }
    public string? PatchResultCommitmentSha256 { get; }
}

public sealed record CampaignScribeExecutionAuthority(
    string ProviderConfigurationId,
    string ModelConfigurationId,
    string ScribeProtocolId,
    string ToolPolicyId);

public sealed record CampaignWorkItemState(
    string WorkItemKey,
    int OuterAttemptCount,
    int CandidateAttemptCount,
    CampaignWorkStatus Status,
    CampaignTrustedProposal? TrustedProposal,
    CampaignWorkClosedOutcome? ClosedOutcome);

public sealed record CampaignProviderReservationExposure(
    int ProviderRequests,
    int InputTokens,
    int UncachedInputTokens,
    int OutputTokens,
    long CostMicrounits,
    int ElapsedMilliseconds);

public abstract record CampaignActiveReservation
{
    private protected CampaignActiveReservation()
    {
    }
}

public sealed record CampaignProviderReservation(
    string WorkItemKey,
    string ScribeRequestSha256,
    DocumentationScribeAttemptId AttemptId,
    CampaignProviderReservationExposure Exposure)
    : CampaignActiveReservation;

public sealed record CampaignPatchReservation : CampaignActiveReservation
{
    internal CampaignPatchReservation(
        string patchRequestSha256,
        long expectedCheckpointRevision,
        int patchAttemptCount,
        long elapsedMilliseconds)
    {
        PatchRequestSha256 = patchRequestSha256;
        ExpectedCheckpointRevision = expectedCheckpointRevision;
        PatchAttemptCount = patchAttemptCount;
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public string PatchRequestSha256 { get; internal init; }
    public long ExpectedCheckpointRevision { get; internal init; }
    public int PatchAttemptCount { get; internal init; }
    public long ElapsedMilliseconds { get; internal init; }
}

public sealed record CampaignChangedFileObservation(
    string Path,
    string OriginalFileSha256,
    string CandidateFileSha256,
    int ChangedDocumentationBlockCount,
    int OriginalDocumentationByteCount,
    int CandidateDocumentationByteCount,
    int OriginalDocumentationLineCount,
    int CandidateDocumentationLineCount);

public sealed record CampaignCandidateObservation
{
    internal CampaignCandidateObservation(
        ImmutableArray<string> acceptedWorkItemKeys,
        string acceptedProjectionCommitmentSha256,
        ImmutableArray<CampaignChangedFileObservation> changedFiles,
        string patchRequestSha256,
        string patchResultCommitmentSha256)
    {
        AcceptedWorkItemKeys = acceptedWorkItemKeys;
        AcceptedProjectionCommitmentSha256 = acceptedProjectionCommitmentSha256;
        ChangedFiles = changedFiles;
        PatchRequestSha256 = patchRequestSha256;
        PatchResultCommitmentSha256 = patchResultCommitmentSha256;
    }

    public ImmutableArray<string> AcceptedWorkItemKeys { get; internal init; }
    public string AcceptedProjectionCommitmentSha256 { get; internal init; }
    public ImmutableArray<CampaignChangedFileObservation> ChangedFiles { get; internal init; }
    public string PatchRequestSha256 { get; internal init; }
    public string PatchResultCommitmentSha256 { get; internal init; }
}

public sealed record CampaignCumulativeOutcome
{
    internal CampaignCumulativeOutcome(
        CampaignCumulativeOutcomeKind kind,
        string patchRequestSha256,
        string? patchResultCommitmentSha256,
        long completedFromCheckpointRevision)
    {
        Kind = kind;
        PatchRequestSha256 = patchRequestSha256;
        PatchResultCommitmentSha256 = patchResultCommitmentSha256;
        CompletedFromCheckpointRevision = completedFromCheckpointRevision;
    }

    public CampaignCumulativeOutcomeKind Kind { get; internal init; }
    public string PatchRequestSha256 { get; internal init; }
    public string? PatchResultCommitmentSha256 { get; internal init; }
    public long CompletedFromCheckpointRevision { get; internal init; }
}

public sealed record CampaignPatchCompletion
{
    internal CampaignPatchCompletion(
        CampaignCandidateObservation? candidateObservation,
        CampaignCumulativeOutcome cumulativeOutcome)
    {
        CandidateObservation = candidateObservation;
        CumulativeOutcome = cumulativeOutcome;
    }

    public CampaignCandidateObservation? CandidateObservation { get; }
    public CampaignCumulativeOutcome CumulativeOutcome { get; }
}

public enum CampaignPatchRejectionDecisionKind
{
    Removable,
    NonRemovable,
}

public sealed class CampaignPatchRejectionDecision
{
    internal CampaignPatchRejectionDecision(
        CampaignPatchRejectionDecisionKind kind,
        CampaignPatchRejectionReduction? reduction)
    {
        Kind = kind;
        Reduction = reduction;
    }

    public CampaignPatchRejectionDecisionKind Kind { get; }
    public CampaignPatchRejectionReduction? Reduction { get; }
}

public sealed class CampaignPatchRejectionReduction
{
    internal CampaignPatchRejectionReduction(
        CampaignCheckpointArtifact predecessor,
        string workItemKey,
        string patchRequestSha256,
        string patchResultCommitmentSha256,
        CampaignWorkClosedOutcome closedOutcome)
    {
        Predecessor = predecessor;
        WorkItemKey = workItemKey;
        PatchRequestSha256 = patchRequestSha256;
        PatchResultCommitmentSha256 = patchResultCommitmentSha256;
        ClosedOutcome = closedOutcome;
    }

    internal CampaignCheckpointArtifact Predecessor { get; }
    internal string WorkItemKey { get; }
    internal string PatchRequestSha256 { get; }
    internal string PatchResultCommitmentSha256 { get; }
    internal CampaignWorkClosedOutcome ClosedOutcome { get; }

    public override string ToString() => nameof(CampaignPatchRejectionReduction);
}

public sealed record CampaignTerminalOutcome(
    CampaignTerminalKind Kind,
    CampaignTerminalReason Reason);

public sealed record CampaignPredecessorReservationSummary(
    string Kind,
    string CorrelationSha256,
    long ConservativeCharge);

public sealed record CampaignPredecessorCandidateSummary(
    int AcceptedCount,
    int DistinctFileCount,
    long OriginalDocumentationByteCount,
    long CandidateDocumentationByteCount,
    long OriginalDocumentationLineCount,
    long CandidateDocumentationLineCount,
    string? PatchRequestSha256,
    string? PatchResultCommitmentSha256);

public sealed record CampaignPredecessorSummary(
    CampaignStateProductRevision ProductRevision,
    CampaignStateSnapshotAuthority Snapshot,
    string CampaignConfigurationCommitmentSha256,
    long FinalCheckpointRevision,
    string FinalCheckpointSha256,
    CampaignTerminalKind TerminalKind,
    CampaignPredecessorReservationSummary? Reservation,
    CampaignPredecessorCandidateSummary Candidate);

public sealed record CampaignCheckpointState
{
    internal CampaignCheckpointState(
        CampaignStateProductRevision productRevision,
        string campaignLineage,
        CampaignStateSnapshotAuthority snapshot,
        long checkpointRevision,
        CampaignStateConfiguredCeilings configuredCeilings,
        CampaignLineageCharges lineageCharges,
        ImmutableArray<CampaignWorkItemState> workItems,
        CampaignActiveReservation? activeReservation,
        CampaignCandidateObservation? candidateObservation,
        CampaignCumulativeOutcome? cumulativeOutcome,
        CampaignTerminalOutcome? terminalOutcome,
        CampaignPredecessorSummary? predecessor)
    {
        ProductRevision = productRevision;
        CampaignLineage = campaignLineage;
        Snapshot = snapshot;
        CheckpointRevision = checkpointRevision;
        ConfiguredCeilings = configuredCeilings;
        LineageCharges = lineageCharges;
        WorkItems = workItems;
        ActiveReservation = activeReservation;
        CandidateObservation = candidateObservation;
        CumulativeOutcome = cumulativeOutcome;
        TerminalOutcome = terminalOutcome;
        Predecessor = predecessor;
    }

    public int CampaignStateVersion => CampaignStateContract.Version;
    public CampaignStateProductRevision ProductRevision { get; }
    public string CampaignLineage { get; }
    public CampaignStateSnapshotAuthority Snapshot { get; }
    public long CheckpointRevision { get; }
    public CampaignStateConfiguredCeilings ConfiguredCeilings { get; }
    public CampaignLineageCharges LineageCharges { get; }
    public ImmutableArray<CampaignWorkItemState> WorkItems { get; }
    public CampaignActiveReservation? ActiveReservation { get; }
    public CampaignCandidateObservation? CandidateObservation { get; }
    public CampaignCumulativeOutcome? CumulativeOutcome { get; }
    public CampaignTerminalOutcome? TerminalOutcome { get; }
    public CampaignPredecessorSummary? Predecessor { get; }
}

public sealed class CampaignCheckpointArtifact
{
    internal CampaignCheckpointArtifact(
        CampaignCheckpointState state,
        byte[] exactUtf8Json,
        string sha256)
    {
        State = state;
        ExactUtf8Json = ImmutableArray.CreateRange(exactUtf8Json);
        Sha256 = sha256;
    }

    public CampaignCheckpointState State { get; }
    public ImmutableArray<byte> ExactUtf8Json { get; }
    public long CheckpointRevision => State.CheckpointRevision;
    public string Sha256 { get; }
}

public sealed class CampaignCheckpointParseResult
{
    internal CampaignCheckpointParseResult(
        CampaignCheckpointArtifact? artifact,
        CampaignStateValidationCode? failureCode)
    {
        Artifact = artifact;
        FailureCode = failureCode;
    }

    public bool IsValid => Artifact is not null;
    public CampaignCheckpointArtifact? Artifact { get; }
    public CampaignStateValidationCode? FailureCode { get; }
}
