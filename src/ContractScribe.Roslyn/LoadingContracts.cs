using System.Collections.Immutable;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Roslyn;

public sealed record RepositoryLoadRequest(
    string RepositoryRoot,
    string InputPath,
    IReadOnlyList<ToolGeneratedSourceInput>? ToolGeneratedSources = null);

public sealed record ToolGeneratedSourceInput(
    string ProjectIdentity,
    string ProducerNamespace,
    string ProducerName,
    string OutputName,
    string SourceText);

public enum RepositoryLoadStatus
{
    Success,
    Failure,
    Cancelled,
}

public sealed record LoaderFact(string Stage, string Code);

public sealed record LoaderDiagnostic(string Stage, string Code, string Severity);

public sealed record ToolchainIdentity(
    string SdkVersion,
    string RuntimeVersion,
    string MsbuildVersion,
    string Architecture);

public sealed class RepositoryLoadOutcome
{
    private RepositoryLoadOutcome(
        RepositoryLoadStatus status,
        LoadedRepositorySession? session,
        ToolchainIdentity? toolchain,
        LoaderFact? primaryFailure,
        IReadOnlyList<LoaderFact> secondaryFacts,
        IReadOnlyList<LoaderDiagnostic> diagnostics)
    {
        Status = status;
        Session = session;
        Toolchain = toolchain;
        PrimaryFailure = primaryFailure;
        SecondaryFacts = secondaryFacts;
        Diagnostics = diagnostics;
    }

    public RepositoryLoadStatus Status { get; }

    public LoadedRepositorySession? Session { get; }
    public ToolchainIdentity? Toolchain { get; }

    public LoaderFact? PrimaryFailure { get; }

    public IReadOnlyList<LoaderFact> SecondaryFacts { get; }

    public IReadOnlyList<LoaderDiagnostic> Diagnostics { get; }

    internal static RepositoryLoadOutcome Success(
        LoadedRepositorySession session,
        IReadOnlyList<LoaderDiagnostic> diagnostics) =>
        new(RepositoryLoadStatus.Success, session, session.Toolchain, null, [], diagnostics);

    internal static RepositoryLoadOutcome Failure(
        LoaderFact failure,
        ToolchainIdentity? toolchain = null,
        IReadOnlyList<LoaderDiagnostic>? diagnostics = null,
        IReadOnlyList<LoaderFact>? secondaryFacts = null) =>
        new(RepositoryLoadStatus.Failure, null, toolchain, failure, secondaryFacts ?? [], diagnostics ?? []);

    internal static RepositoryLoadOutcome Cancelled(
        ToolchainIdentity? toolchain = null,
        IReadOnlyList<LoaderDiagnostic>? diagnostics = null,
        IReadOnlyList<LoaderFact>? secondaryFacts = null) =>
        new(
            RepositoryLoadStatus.Cancelled,
            null,
            toolchain,
            new LoaderFact("cancellation", "loader.cancelled"),
            secondaryFacts ?? [],
            diagnostics ?? []);
}

public sealed class LoadedRepositorySession : IAsyncDisposable, IDisposable
{
    private readonly object documentationPatchGate = new();
    private readonly IDisposable workspace;
    private DocumentationPatchRepositoryPolicy? documentationPatchPolicy;
    private bool disposed;

    internal LoadedRepositorySession(
        RepositoryContextRef repositoryContextRef,
        string physicalRepositoryRoot,
        string inputIdentity,
        ToolchainIdentity toolchain,
        IReadOnlyList<LoadedProject> projects,
        IReadOnlyList<GeneratedSourceFact> generatedSources,
        IDisposable workspace,
        DocumentationScribeContextPhysicalIdentity? repositoryRootIdentity = null)
    {
        RepositoryContextRef = repositoryContextRef;
        PhysicalRepositoryRoot = physicalRepositoryRoot;
        InputIdentity = inputIdentity;
        Toolchain = toolchain;
        Projects = projects;
        GeneratedSources = generatedSources;
        RepositoryRootIdentity = repositoryRootIdentity;
        this.workspace = workspace;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    internal string PhysicalRepositoryRoot { get; }

    internal DocumentationScribeContextPhysicalIdentity? RepositoryRootIdentity { get; }

    public string InputIdentity { get; }

    public ToolchainIdentity Toolchain { get; }

    public IReadOnlyList<LoadedProject> Projects { get; }

    public IReadOnlyList<GeneratedSourceFact> GeneratedSources { get; }

    internal bool IsDisposed
    {
        get
        {
            lock (documentationPatchGate)
            {
                return disposed;
            }
        }
    }

    public DocumentationPatchRepositoryBaselineCaptureResult CaptureDocumentationPatchRepositoryBaseline(
        CancellationToken cancellationToken = default)
    {
        DocumentationPatchRepositoryPolicy? policy;
        lock (documentationPatchGate)
        {
            policy = disposed ? null : documentationPatchPolicy;
        }

        if (policy is null)
        {
            return new DocumentationPatchRepositoryBaselineCaptureResult(
                DocumentationPatchRepositoryBaselineStatus.Stale,
                "patch.stale.repository-context",
                null);
        }

        return DocumentationPatchRepositoryBaselineCapture.Capture(
            this,
            policy,
            cancellationToken);
    }

    internal DocumentationPatchRepositoryBaselineCaptureResult CaptureDocumentationPatchResolutionBaseline(
        CancellationToken cancellationToken = default)
    {
        DocumentationPatchRepositoryPolicy? policy;
        lock (documentationPatchGate)
        {
            policy = disposed ? null : documentationPatchPolicy;
        }

        if (policy is null)
        {
            return new DocumentationPatchRepositoryBaselineCaptureResult(
                DocumentationPatchRepositoryBaselineStatus.Stale,
                "patch.stale.repository-context",
                null);
        }

        return DocumentationPatchRepositoryBaselineCapture.CaptureForResolution(
            this,
            policy,
            cancellationToken);
    }

    internal void SealDocumentationPatchRepositoryPolicy(
        DocumentationPatchRepositoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (documentationPatchGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (documentationPatchPolicy is not null)
            {
                throw new InvalidOperationException(
                    "The documentation patch repository policy is already sealed.");
            }

            documentationPatchPolicy = policy;
        }
    }

    internal void SealDocumentationPatchRepositoryPolicyForTests(
        IEnumerable<string>? allowedOutputRoots = null)
    {
        SealDocumentationPatchRepositoryPolicy(
            DocumentationPatchRepositoryPolicy.CreateForTests(
                PhysicalRepositoryRoot,
                Projects,
                allowedOutputRoots));
    }

    internal bool IsDocumentationPatchAuthorityAvailable(
        DocumentationPatchRepositoryPolicy policy)
    {
        lock (documentationPatchGate)
        {
            return !disposed && ReferenceEquals(documentationPatchPolicy, policy);
        }
    }

    public void Dispose()
    {
        lock (documentationPatchGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        workspace.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ClassifiedRepositorySession
{
    private readonly LoadedRepositorySession? classificationSession;

    internal ClassifiedRepositorySession(
        LoadedRepositorySession repositorySession,
        ClassificationOutcome classification)
        : this(repositorySession, classification, null)
    {
    }

    private ClassifiedRepositorySession(
        LoadedRepositorySession repositorySession,
        ClassificationOutcome classification,
        LoadedRepositorySession? classificationSession)
    {
        RepositorySession = repositorySession;
        Classification = classification;
        this.classificationSession = classificationSession;
    }

    public LoadedRepositorySession RepositorySession { get; }

    public ClassificationOutcome Classification { get; }

    internal bool IsBoundToClassificationSession =>
        ReferenceEquals(RepositorySession, classificationSession);

    internal static ClassifiedRepositorySession Bind(
        LoadedRepositorySession repositorySession,
        ClassificationOutcome classification) =>
        new(
            repositorySession,
            classification,
            repositorySession);
}

public sealed class ObservedRepositorySession
{
    private readonly ClassifiedRepositorySession observationSession;

    private ObservedRepositorySession(
        ClassifiedRepositorySession observationSession,
        DocumentationObservationOutcome observation)
    {
        this.observationSession = observationSession;
        Observation = observation;
    }

    public DocumentationObservationOutcome Observation { get; }

    public DocumentationObservationRunStatus Status => Observation.Status;

    public DocumentationObservationSet? ObservationSet => Observation.ObservationSet;

    public DocumentationObservationFailure? PrimaryFailure => Observation.PrimaryFailure;

    public ImmutableArray<DocumentationObservationDiagnostic> Diagnostics =>
        Observation.Diagnostics;

    internal bool IsBoundToObservationSession(
        ClassifiedRepositorySession session) =>
        ReferenceEquals(observationSession, session);

    internal ClassifiedRepositorySession ClassificationSession => observationSession;

    internal static ObservedRepositorySession Bind(
        ClassifiedRepositorySession observationSession,
        DocumentationObservationOutcome observation) =>
        new(observationSession, observation);

    public static implicit operator DocumentationObservationOutcome(
        ObservedRepositorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Observation;
    }
}

public enum LoadedProjectRole
{
    AuditRoot,
    DependencyOnly,
}

public sealed class LoadedProject
{
    internal LoadedProject(
        string projectIdentity,
        string targetFramework,
        string compilationContextRef,
        LoadedProjectRole role,
        IReadOnlyList<string> projectReferences,
        Project project,
        Compilation compilation,
        IReadOnlyDictionary<SyntaxTree, LoadedSourceTree> sourceTrees)
    {
        ProjectIdentity = projectIdentity;
        TargetFramework = targetFramework;
        CompilationContextRef = compilationContextRef;
        Role = role;
        ProjectReferences = projectReferences;
        Project = project;
        Compilation = compilation;
        SourceTrees = sourceTrees;
    }

    public string ProjectIdentity { get; }

    public string TargetFramework { get; }

    public string CompilationContextRef { get; }

    public LoadedProjectRole Role { get; }

    public IReadOnlyList<string> ProjectReferences { get; }

    internal Project Project { get; }

    internal Compilation Compilation { get; }

    internal IReadOnlyDictionary<SyntaxTree, LoadedSourceTree> SourceTrees { get; }
}

public sealed record GeneratedSourceFact(
    string ProjectIdentity,
    string CompilationContextRef,
    string ProducerId,
    string OutputId,
    string SourceSha256,
    string SourceText);

internal enum LoadedSourceKind
{
    Repository,
    SourceGenerator,
    ToolGenerated,
}

internal sealed record LoadedSourceTree(
    LoadedSourceKind Kind,
    string? RepositoryPath,
    string? PhysicalSourceIdentity,
    GeneratedSourceFact? GeneratedSource);

internal sealed record GeneratedSourceBinding(
    SyntaxTree SyntaxTree,
    LoadedSourceKind Kind,
    GeneratedSourceFact Fact);
