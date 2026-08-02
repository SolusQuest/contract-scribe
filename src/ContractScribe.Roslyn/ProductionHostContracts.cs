using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed record ProductionAuditRequest(
    string RepositoryRoot,
    string InputPath,
    byte[] PolicyBytes,
    ResolvedPublicationTarget PublicationTarget,
    HostBuildProvenance ProvenanceAssertion,
    string? AuditTemporaryRoot = null,
    string? OutputStagingRoot = null,
    IReadOnlyList<ToolGeneratedSourceInput>? ToolGeneratedSources = null);

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
    Func<RepositoryLoadRequest, CancellationToken, Task<RepositoryLoadOutcome>>? RepositoryLoad = null,
    Func<LoadedRepositorySession, Task>? Shutdown = null,
    Func<HostStage, CancellationToken, Task>? StageBoundary = null)
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
