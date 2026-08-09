using System.ComponentModel;
using Microsoft.CodeAnalysis.MSBuild;

namespace ContractScribe.Roslyn;

internal enum LoaderExecutionPhase
{
    None,
    RequestValidation,
    PathResolution,
    BaselineCapture,
    ToolchainRegistration,
    InputParsing,
    GraphEvaluation,
    WorkspaceCreation,
    WorkspaceOpen,
    RemoteEvaluateReported,
    RemoteBuildReported,
    RemoteResolveReported,
    RemoteUnknownReported,
    WorkspaceOpenCompleted,
    WorkspaceSemanticProtection,
    WorkspaceLoad,
    Compilation,
    GeneratedFacts,
    TerminalValidation,
    SessionConstruction,
    SessionTransfer,
    Cleanup,
    FinalInventory,
}

internal enum LoaderExceptionBoundary
{
    PostRegistrationLoad,
    RepositoryLoad,
    WorkspaceCleanup,
    FinalInventory,
}

internal enum LoaderExceptionRole
{
    Primary,
    Cleanup,
}

internal sealed record LoaderLifecycleObservation(
    bool PathsResolved,
    bool BaselineCaptured,
    bool ToolchainSelected,
    bool WorkspaceCreated,
    bool WorkspaceOpenStarted,
    bool WorkspaceOpenCompleted,
    bool SessionConstructed,
    bool SessionTransferred,
    bool CleanupStarted,
    bool CleanupCompleted);

internal sealed record LoaderExceptionObservation(
    LoaderExceptionRole Role,
    LoaderExceptionBoundary Boundary,
    LoaderExecutionPhase Phase,
    IReadOnlyList<string> TypeChain,
    int HResult,
    int? NativeErrorCode,
    LoaderLifecycleObservation Lifecycle);

internal sealed record LoaderExecutionSnapshot(
    LoaderExecutionPhase Phase,
    LoaderLifecycleObservation Lifecycle,
    IReadOnlyList<LoaderExceptionObservation> Exceptions);

internal sealed class LoaderExecutionTrace
{
    internal const int MaximumExceptionRecords = 4;
    internal const int MaximumTypeDepth = 4;
    internal const int MaximumTypeNameLength = 160;

    private readonly object gate = new();
    private readonly List<LoaderExceptionObservation> exceptions = [];
    private LoaderExecutionPhase phase;
    private bool pathsResolved;
    private bool baselineCaptured;
    private bool toolchainSelected;
    private bool workspaceCreated;
    private bool workspaceOpenStarted;
    private bool workspaceOpenCompleted;
    private bool sessionConstructed;
    private bool sessionTransferred;
    private bool cleanupStarted;
    private bool cleanupCompleted;

    public void Enter(LoaderExecutionPhase next)
    {
        lock (gate)
        {
            phase = next;
        }
    }

    public void MarkPathsResolved() => Set(ref pathsResolved);

    public void MarkBaselineCaptured() => Set(ref baselineCaptured);

    public void MarkToolchainSelected() => Set(ref toolchainSelected);

    public void MarkWorkspaceCreated() => Set(ref workspaceCreated);

    public void MarkWorkspaceOpenStarted() => Set(ref workspaceOpenStarted);

    public void MarkWorkspaceOpenCompleted() => Set(ref workspaceOpenCompleted);

    public void MarkSessionConstructed() => Set(ref sessionConstructed);

    public void MarkSessionTransferred() => Set(ref sessionTransferred);

    public void MarkCleanupStarted()
    {
        lock (gate)
        {
            phase = LoaderExecutionPhase.Cleanup;
            cleanupStarted = true;
        }
    }

    public void MarkCleanupCompleted() => Set(ref cleanupCompleted);

    public void Observe(ProjectLoadOperation operation) => Enter(operation switch
    {
        ProjectLoadOperation.Evaluate => LoaderExecutionPhase.RemoteEvaluateReported,
        ProjectLoadOperation.Build => LoaderExecutionPhase.RemoteBuildReported,
        ProjectLoadOperation.Resolve => LoaderExecutionPhase.RemoteResolveReported,
        _ => LoaderExecutionPhase.RemoteUnknownReported,
    });

    public void RecordPrimary(LoaderExceptionBoundary boundary, Exception exception) =>
        Record(LoaderExceptionRole.Primary, boundary, exception);

    public void RecordCleanup(LoaderExceptionBoundary boundary, Exception exception) =>
        Record(LoaderExceptionRole.Cleanup, boundary, exception);

    public LoaderExecutionSnapshot Snapshot()
    {
        lock (gate)
        {
            return new LoaderExecutionSnapshot(
                phase,
                Lifecycle(),
                exceptions.ToArray());
        }
    }

    private void Set(ref bool field)
    {
        lock (gate)
        {
            field = true;
        }
    }

    private void Record(
        LoaderExceptionRole role,
        LoaderExceptionBoundary boundary,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (gate)
        {
            if (exceptions.Count == MaximumExceptionRecords)
            {
                return;
            }

            var chain = new List<string>(MaximumTypeDepth);
            for (var current = exception;
                 current is not null && chain.Count < MaximumTypeDepth;
                 current = current.InnerException)
            {
                var name = current.GetType().FullName ?? current.GetType().Name;
                chain.Add(name.Length <= MaximumTypeNameLength
                    ? name
                    : name[..MaximumTypeNameLength]);
            }

            exceptions.Add(new LoaderExceptionObservation(
                role,
                boundary,
                phase,
                chain,
                exception.HResult,
                exception is Win32Exception native ? native.NativeErrorCode : null,
                Lifecycle()));
        }
    }

    private LoaderLifecycleObservation Lifecycle() => new(
        pathsResolved,
        baselineCaptured,
        toolchainSelected,
        workspaceCreated,
        workspaceOpenStarted,
        workspaceOpenCompleted,
        sessionConstructed,
        sessionTransferred,
        cleanupStarted,
        cleanupCompleted);
}

internal sealed class LoaderProjectLoadProgress(LoaderExecutionTrace trace)
    : IProgress<ProjectLoadProgress>
{
    public void Report(ProjectLoadProgress value) => trace.Observe(value.Operation);
}
