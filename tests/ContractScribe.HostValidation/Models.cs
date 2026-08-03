using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContractScribe.HostValidation;

public sealed record ProtocolManifest(
    string FormatVersion,
    string ProtocolId,
    BaselineIdentity Baseline,
    IReadOnlyList<string> ArtifactInventory,
    IReadOnlyList<CellDefinition> RequiredCells,
    SubjectSourceContract SubjectSourceContract,
    TaxonomyDefinition Taxonomies,
    ExecutionContract ExecutionContract,
    PublicSafetyPolicy PublicSafety,
    ArtifactIdentity NetworkEvidenceProfile,
    IReadOnlyList<string> RequiredValidators);

public sealed record BaselineIdentity(
    string CoordinatingIssue,
    string ContractRevision,
    string Disposition,
    string? MergeCommit,
    string ContractManifest,
    string ContractManifestSha256,
    PredecessorBaselineIdentity Predecessor);

public sealed record PredecessorBaselineIdentity(
    string CoordinatingIssue,
    string ContractRevision,
    string MergeCommit,
    string ContractManifest,
    string ContractManifestSha256);

public sealed record CellDefinition(
    string CellId,
    string RunnerOs,
    string Architecture,
    string Rid,
    string RunnerImagePolicy);

public sealed record SubjectSourceContract(
    IReadOnlyList<string> SourceRoots,
    string FailureRegistry,
    string CalibratedBounds,
    string BuildRecipe,
    string CommandContract,
    string ContractBaseline,
    string EnvironmentPolicy,
    string Workflow);

public sealed record TaxonomyDefinition(
    IReadOnlyList<string> ProcessStart,
    IReadOnlyList<string> ProcessTermination,
    IReadOnlyList<string> AuditOutcome,
    IReadOnlyList<string> ExecutionOutcome,
    IReadOnlyList<string> VectorVerdict,
    IReadOnlyList<string> ValidationOutcome,
    IReadOnlyList<string> ValidationPrecedence,
    IReadOnlyList<string> EnforcementClass);

public sealed record ExecutionContract(
    string AdapterVersion,
    string RequestEncoding,
    string ResponseEncoding,
    int StandardOutputByteLimit,
    int StandardErrorByteLimit,
    int ResponseByteLimit,
    int EvidenceByteLimit,
    int SubjectTimeoutSeconds,
    string RetryPolicy,
    string RepositoryObserverClaim,
    string ProcessObserverClaim);

public sealed record PublicSafetyPolicy(
    string NetworkClaimSetId,
    IReadOnlyList<NetworkClaimDefinition> NetworkClaimSetMembers,
    IReadOnlyList<string> ProhibitedClaims,
    IReadOnlyList<string> PublicArtifactAllowlist,
    IReadOnlyList<string> StableDiagnosticCodes);

public sealed record NetworkClaimDefinition(
    string ClaimId,
    string Text);

public sealed record NetworkEvidenceProfileManifest(
    string FormatVersion,
    string ProfileId,
    string ClaimSetId,
    IReadOnlyList<NetworkEvidenceMethodDefinition> Methods,
    string RecorderActivationRecord,
    IReadOnlyList<string> CoveredSourceIndirections);

public sealed record NetworkEvidenceMethodDefinition(
    string MethodId,
    int MethodVersion,
    string CoverageLimitationId);

public sealed record NetworkEvidenceObservation(
    string ProfileId,
    string ClaimSetId,
    IReadOnlyList<NetworkEvidenceMethodResult> Methods);

public sealed record NetworkEvidenceMethodResult(
    string MethodId,
    int MethodVersion,
    string InputIdentity,
    string CoverageLimitationId,
    string Status,
    string ObservationCode,
    string? CauseClass);

public sealed record VectorCatalog(
    string FormatVersion,
    IReadOnlyList<VectorDefinition> Vectors);

public sealed record VectorDefinition(
    string VectorId,
    string Category,
    string ExecutorKind,
    IReadOnlyList<string> Cells,
    int InvocationCount,
    bool FreshProcessPerInvocation,
    IReadOnlyList<string> RunIds,
    IReadOnlyList<string> EqualityFields,
    bool CrossCellEquality,
    string Fixture,
    string ExpectedObservation,
    string ExpectedEnforcementClass,
    string SupportDisposition,
    IReadOnlyList<string> ObserverRequirements,
    IReadOnlyList<string> ProtectedInputClasses);

public sealed record ExpectedRun(string CellId, string VectorId, string RunId);

public sealed record ArtifactLock(
    string FormatVersion,
    string BundleId,
    IReadOnlyList<ArtifactIdentity> Entries);

public sealed record ArtifactIdentity(string Path, string Sha256);

public sealed record ProtectedInputManifest(
    string FormatVersion,
    IReadOnlyList<string> Roots,
    IReadOnlyList<ArtifactIdentity> Entries);

public sealed record ReviewRecord(
    string FormatVersion,
    string ReviewId,
    string BundleId,
    string? ReviewedHead,
    string? ReviewerKind,
    string? RelaySessionId,
    string? RelayTaskId,
    string Verdict,
    IReadOnlyList<string> BlockingFindingIds,
    string? ReviewedAtUtc);

public sealed record SubjectRequest(
    string FormatVersion,
    string SubjectKind,
    string VectorId,
    string RunId,
    string RepositoryRoot,
    string ResponsePath,
    string? ControlRoot,
    IReadOnlyList<string> SynchronizationGates,
    string ControlAction,
    string? NetworkOperationLogPath = null,
    string? TransitionLogPath = null,
    string? AuditTemporaryRoot = null,
    TemporaryDiskGateContract? TemporaryDiskGate = null,
    PublicationFault? PublicationFault = null,
    PostTerminalAttempt? PostTerminalAttempt = null);

public sealed record PublicationFault(
    string Operation,
    int Occurrence,
    string Failure,
    string? StagingRelativePath);

public sealed record PostTerminalAttempt(
    string ExecutionOutcome,
    string Timing,
    int Occurrence);

public sealed record TemporaryDiskGateContract(
    string TemporaryWorkRoot,
    string OutputStagingRoot,
    string FreezeSentinelName,
    string ReleaseSentinelName);

public sealed record SubjectResponse(
    string FormatVersion,
    string VectorId,
    string RunId,
    string ProcessStart,
    string ProcessTermination,
    string? AuditOutcome,
    string? ExecutionOutcome,
    string? FailureRegistryIdentity,
    string? FailureCode,
    string? FailureStage,
    string TerminalState,
    string ArtifactState,
    string EnforcementClass,
    string ObservationCode,
    CanonicalResultCommitment? CanonicalResult,
    HostObservationFacts? HostFacts = null);

public sealed record CanonicalResultCommitment(
    string Sha256,
    long ByteCount,
    string Encoding,
    bool Canonical);

public sealed record ObservedAuditResultFacts(
    int AuditResultVersion,
    int PolicyConfigurationVersion,
    int TaxonomyRegistryVersion,
    string TargetProfile,
    IReadOnlyList<string> AuditOutcomes);

public sealed record HostObservationFacts(
    string SourceConfigurationId,
    string HostRevision,
    string ContractBaselineSha256,
    string FailureRegistrySha256,
    string CalibratedBoundsSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedSdk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedRuntime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedMsbuild,
    IReadOnlyList<NormalizedDiagnosticFact> NormalizedDiagnosticFacts,
    OutputCommitFact OutputCommit,
    IReadOnlyList<MeasuredBoundFact> MeasuredBounds,
    LoaderObservationFact? LoaderFact = null,
    string ToolchainSelectionState = "selected");

public sealed record NormalizedDiagnosticFact(string Code, string Stage);

public sealed record OutputCommitFact(string Status, string? Sha256);

public sealed record LoaderObservationFact(
    string Code,
    string Disposition,
    bool SelectedOrDefaultTargetFramework,
    bool PartialResultProduced);

public sealed record MeasuredBoundFact(
    string Name,
    string Unit,
    long Measured,
    long Threshold,
    string EnforcementClass);

public sealed record ProcessExecutionResult(
    int? ExitCode,
    string ProcessStart,
    string ProcessTermination,
    byte[] StandardOutput,
    byte[] StandardError,
    bool StandardOutputOverflow,
    bool StandardErrorOverflow,
    bool StandardOutputValidUtf8,
    bool StandardErrorValidUtf8,
    bool TimedOut,
    bool ControlCompleted,
    string? ControlOutcome,
    bool ObservationComplete,
    IReadOnlyList<ObservedProcess> ObservedProcesses,
    string? KillRequestOutcome = null,
    string? FinalPlatformTerminationStatus = null,
    string? NativeTerminationKind = null,
    long? NativeTerminationCode = null,
    TemporaryDiskHighWaterEvidence? TemporaryDiskHighWater = null,
    CanonicalResultCommitment? StagedCanonical = null);

public sealed record ControlExecutionResult(
    bool Completed,
    string? Outcome,
    NativeTerminationEvidence? NativeTermination = null,
    TemporaryDiskHighWaterEvidence? TemporaryDiskHighWater = null,
    CanonicalResultCommitment? StagedCanonical = null);

public sealed record NativeTerminationEvidence(
    string Kind,
    int? ManagedExitCode,
    long? Code,
    string KillRequestOutcome,
    bool CausalMatch);

public sealed record TemporaryDiskHighWaterEvidence(
    string Quantity,
    string GovernedRootsIdentity,
    string IntervalIdentity,
    long TemporaryWorkBytes,
    long OutputStagingBytes,
    long TotalBytes,
    bool ObserverComplete,
    bool RetentionBreach);

public sealed record SubjectControl(
    string ControlRoot,
    string GateName,
    string Action,
    TimeSpan GateTimeout,
    TimeSpan ActionDelay = default,
    bool WaitForExitBeforeAction = false,
    Func<Action, MonotonicDeadline, TemporaryDiskHighWaterEvidence>?
        MeasureTemporaryDisk = null,
    Func<TimeSpan, Action, CancellationToken, Task<CanonicalResultCommitment>>?
        ObserveStagedCanonical = null);

public sealed record ExecutionSubjectManifest(
    string FormatVersion,
    string BundleId,
    string SubjectKind,
    string ImplementationOwner,
    string EntryPointContract,
    SubjectSourceConfiguration SourceConfiguration,
    ValidationAttemptIdentity ValidationAttempt,
    IReadOnlyList<ExecutionCell> Cells);

public sealed record SubjectSourceConfiguration(
    string SourceConfigurationId,
    string HostRevision,
    string DeclaredOperationInventoryId,
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<ArtifactIdentity> SourceAndBuildInputs,
    ArtifactIdentity FailureRegistry,
    ArtifactIdentity CalibratedBounds,
    ArtifactIdentity BuildRecipe,
    ArtifactIdentity CommandContract,
    ArtifactIdentity ContractBaseline,
    ArtifactIdentity EnvironmentPolicy,
    ArtifactIdentity Workflow);

public sealed record ExecutionCell(
    CellMaterialization Materialization,
    string LaunchKind,
    string EntryPoint,
    IReadOnlyList<string> ArgumentPrefix,
    IReadOnlyList<FixtureRealization> Fixtures);

public sealed record FixtureRealization(
    string VectorId,
    string ExecutorKind,
    string RepositoryRoot,
    string RepositoryIdentitySha256,
    bool CapabilityAvailable,
    string? BlockedReasonCode,
    string? Executable,
    IReadOnlyList<string> Arguments,
    string? ExecutableSha256,
    IReadOnlyList<ArtifactIdentity> ArrangementInputs,
    IReadOnlyList<string> AllowedDesignTimeRoots,
    string ProcessObservationMode,
    string? ResultPath,
    string ResultPrestate,
    IReadOnlyList<RunWorkingDirectory> RunWorkingDirectories,
    string? ExternalCause,
    IReadOnlyList<ProcessIdentityRule>? ProcessIdentityRegistry = null);

public sealed record RunWorkingDirectory(string RunId, string Mode);

public sealed record ProcessIdentityRule(
    string FingerprintSha256,
    string ArtifactKind,
    string EntryPointSha256);

public sealed record ObservedProcess(
    int ProcessId,
    int ParentProcessId,
    string Role,
    string ImageName);

public sealed record ProcessInstanceIdentity(
    int ProcessId,
    long StartIdentity);

public sealed record ProcessSnapshotIdentity(
    ProcessInstanceIdentity Identity,
    int ParentProcessId);

public sealed record ProcessTerminationTarget(
    ProcessInstanceIdentity Identity,
    int ParentProcessId,
    int Depth);

public sealed record ProcessTerminationPlan(
    ProcessInstanceIdentity Root,
    IReadOnlyList<ProcessTerminationTarget> Descendants,
    bool Complete);

public sealed record RepositorySnapshot(
    IReadOnlyDictionary<string, string> ProtectedFiles,
    IReadOnlyDictionary<string, string> OtherFiles,
    IReadOnlyDictionary<string, string> AllowedDesignTimeFiles,
    IReadOnlyDictionary<string, long>? ProtectedByteCounts = null,
    IReadOnlyDictionary<string, long>? OtherByteCounts = null,
    IReadOnlyDictionary<string, long>? AllowedDesignTimeByteCounts = null);

public sealed record RepositoryDelta(
    IReadOnlyList<string> ProtectedCreated,
    IReadOnlyList<string> ProtectedDeleted,
    IReadOnlyList<string> ProtectedChanged,
    IReadOnlyList<string> OtherCreated,
    IReadOnlyList<string> OtherDeleted,
    IReadOnlyList<string> OtherChanged,
    IReadOnlyList<string> AllowedDesignTimeCreated,
    IReadOnlyList<string> AllowedDesignTimeDeleted,
    IReadOnlyList<string> AllowedDesignTimeChanged,
    long ProtectedCreatedOrChangedBytes = 0,
    long OtherCreatedOrChangedBytes = 0,
    long AllowedDesignTimeCreatedOrChangedBytes = 0);

public sealed record CellEvidence(
    string FormatVersion,
    string BundleId,
    string NetworkClaimSetId,
    string ReviewId,
    string SourceConfigurationId,
    string SubjectManifestSha256,
    ValidationAttemptIdentity ValidationAttempt,
    CellMaterialization Cell,
    IReadOnlyList<RunEvidence> Runs,
    string Outcome);

public sealed record ValidationAttemptIdentity(
    string Workflow,
    string WorkflowRevision,
    string WorkflowRunId,
    int RunAttempt,
    string ValidationExecutionSha,
    string HostRevision);

public sealed record AggregateFinalizationIdentity(
    string MatrixResult,
    string EvidencePublicationBaseRevision);

public sealed record CellMaterialization(
    string CellId,
    string JobId,
    string JobUrl,
    string RunnerImage,
    string Rid,
    string Architecture,
    string SelectedSdk,
    string SelectedRuntime,
    string SelectedMsbuild,
    IReadOnlyList<ArtifactIdentity> BuiltArtifacts);

public sealed record RunEvidence(
    string VectorId,
    string RunId,
    string Verdict,
    string ExpectedObservation,
    string ObservedObservation,
    string ExpectedEnforcementClass,
    string ObservedEnforcementClass,
    SubjectResponse? Subject,
    ProcessObservation Process,
    CanonicalResultCommitment? ObservedCanonicalResult,
    ObservedAuditResultFacts? ObservedAuditResult,
    RepositoryDelta RepositoryDelta,
    IReadOnlyList<ObservedProcess> ObservedProcesses,
    IReadOnlyList<string> DiagnosticCodes,
    PublicationArtifactObservation? PublicationArtifactObservation = null);

public sealed record PublicationArtifactObservation(
    CanonicalResultCommitment? PreRunCanonical,
    CanonicalResultCommitment? PostRunCanonical,
    string PostRunAttribution,
    CanonicalResultCommitment? StagedCanonical,
    string StagingDisposition);

public sealed record ProcessObservation(
    int? ExitCode,
    string ProcessStart,
    string ProcessTermination,
    bool TimedOut,
    bool ControlCompleted,
    bool ObservationComplete,
    string? ObservedGateName = null,
    string? ObservedControlAction = null,
    bool PostGateSampleObserved = false,
    string? ObservedControlOutcome = null,
    string? NetworkOperationRecorderState = null,
    IReadOnlyList<string>? TransitionEvents = null,
    long StandardOutputByteCount = 0,
    long StandardErrorByteCount = 0,
    string? KillRequestOutcome = null,
    string? FinalPlatformTerminationStatus = null,
    string? NativeTerminationKind = null,
    long? NativeTerminationCode = null,
    TemporaryDiskHighWaterEvidence? TemporaryDiskHighWater = null,
    NetworkEvidenceObservation? NetworkEvidence = null);

public sealed record AggregateEvidence(
    string FormatVersion,
    string BundleId,
    string NetworkClaimSetId,
    string ReviewId,
    string SourceConfigurationId,
    ValidationAttemptIdentity ValidationAttempt,
    AggregateFinalizationIdentity Finalization,
    IReadOnlyList<CellAggregate> Cells,
    string Outcome,
    IReadOnlyList<string> Supersedes);

public sealed record EvidencePublicationRecord(
    string FormatVersion,
    string BundleId,
    string NetworkClaimSetId,
    string ReviewId,
    string SourceConfigurationId,
    ValidationAttemptIdentity ValidationAttempt,
    string EvidenceRecordRevision,
    IReadOnlyList<ArtifactIdentity> PublishedEvidence);

public sealed record CellAggregate(
    string CellId,
    string EvidenceSha256,
    string Outcome);

public sealed record IncompleteEvidence(
    string FormatVersion,
    string BundleId,
    string NetworkClaimSetId,
    string ReviewId,
    string SourceConfigurationId,
    ValidationAttemptIdentity ValidationAttempt,
    string? CellId,
    string Classification,
    IReadOnlyList<string> DiagnosticCodes,
    bool Immutable);

public static class ModelExtensions
{
    public static IEnumerable<ExpectedRun> ExpandExpectedRuns(this VectorCatalog catalog) =>
        catalog.Vectors.SelectMany(vector =>
            vector.Cells.SelectMany(cell =>
                vector.RunIds.Select(runId => new ExpectedRun(cell, vector.VectorId, runId))));

    public static JsonElement ToElement<T>(this T value) =>
        JsonSerializer.SerializeToElement(value, CanonicalJson.SerializerOptions);
}
