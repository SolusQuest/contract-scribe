using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed record ProductionAuditRequest(
    string RepositoryRoot,
    string InputPath,
    byte[] PolicyBytes,
    ResolvedPublicationTarget? PublicationTarget,
    string? AuditTemporaryRoot = null,
    string? OutputStagingRoot = null,
    IReadOnlyList<ToolGeneratedSourceInput>? ToolGeneratedSources = null,
    bool PublishResult = true);

internal sealed record ProductionRepositorySessionBundle(
    ResolvedRepositoryPaths ResolvedPaths,
    PolicyDocumentV1 Policy,
    LoadedRepositorySession Session,
    ClassifiedRepositorySession Classified,
    ClassificationSet Classifications,
    ObservedRepositorySession Observed,
    PolicyEvidenceExtractionOutcome Evidence,
    IReadOnlyList<AuditRecordInput> AuditInputs,
    AuditDocument Audit,
    byte[] CanonicalAudit,
    HostToolchainFact Toolchain,
    LoaderFact? LoaderFact);

internal sealed record ProductionAuditOutcome(
    HostTerminalRecord Terminal,
    byte[]? CanonicalResult,
    LoaderFact? LoaderFact,
    IReadOnlyList<string> TransitionEvents);

internal enum ProductionHostControlPoint
{
    BeforeCommit,
    BeforePublicationDecision,
    AfterCommit,
    LateCompletion,
    PublicationStagingReady,
    ProcessObservation,
    TemporaryDiskHighWater,
}

internal enum ProductionHostFault
{
    None,
    EnvironmentUnavailable,
    LoadFailure,
    AuditError,
    PublicationInvalidation,
    PublicationFinalization,
    PublicationCleanup,
}

internal enum ProductionLateAttemptKind
{
    LateCompletion,
    CompetingTerminal,
}

internal sealed record ProductionAuditHostControls(
    ProductionHostFault Fault = ProductionHostFault.None,
    Func<ProductionHostControlPoint, CancellationToken, Task>? Gate = null,
    Action<string>? Transition = null,
    Func<CancellationToken, Task>? LateCompletion = null,
    ProductionLateAttemptKind LateAttemptKind = ProductionLateAttemptKind.LateCompletion,
    Func<string, TimeSpan?>? DeadlineOverride = null,
    Func<CancellationToken, Task<RegisteredToolchain>>? SdkDiscovery = null,
    Func<ToolchainProcessMeter>? ProcessMeterFactory = null,
    Func<RepositoryLoadRequest, CancellationToken, Task<RepositoryLoadOutcome>>? RepositoryLoad = null,
    Func<LoadedRepositorySession, Task>? Shutdown = null,
    Func<HostStage, CancellationToken, Task>? StageBoundary = null,
    Action? BeforeInvalidation = null,
    Action? BeforeAtomicRename = null,
    Action<HostTerminalRecord>? AfterCauseAccepted = null,
    Action<HostToolchainFact>? AfterToolchainSelection = null,
    Func<ProductionRepositorySessionBundle, CancellationToken, Task>? SessionConsumer = null)
{
    public Task ReachAsync(
        ProductionHostControlPoint point,
        CancellationToken cancellationToken) =>
        Gate?.Invoke(point, cancellationToken) ?? Task.CompletedTask;

    public void Record(string transition) => Transition?.Invoke(transition);

    public Task ReachStageAsync(HostStage stage, CancellationToken cancellationToken) =>
        StageBoundary?.Invoke(stage, cancellationToken) ?? Task.CompletedTask;

    public TimeSpan Deadline(string boundName) =>
        DeadlineOverride?.Invoke(boundName)
        ?? TimeSpan.FromMilliseconds(HostContractResources.RequireBound(boundName));
}
