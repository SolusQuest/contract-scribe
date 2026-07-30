using Microsoft.CodeAnalysis;
using ContractScribe.Core;

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
    private readonly IDisposable workspace;

    internal LoadedRepositorySession(
        string repositoryIdentity,
        string inputIdentity,
        ToolchainIdentity toolchain,
        IReadOnlyList<LoadedProject> projects,
        IReadOnlyList<GeneratedSourceFact> generatedSources,
        IDisposable workspace)
    {
        RepositoryIdentity = repositoryIdentity;
        InputIdentity = inputIdentity;
        Toolchain = toolchain;
        Projects = projects;
        GeneratedSources = generatedSources;
        this.workspace = workspace;
    }

    public string RepositoryIdentity { get; }

    public string InputIdentity { get; }

    public ToolchainIdentity Toolchain { get; }

    public IReadOnlyList<LoadedProject> Projects { get; }

    public IReadOnlyList<GeneratedSourceFact> GeneratedSources { get; }

    public void Dispose() => workspace.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ClassifiedRepositorySession
{
    internal ClassifiedRepositorySession(
        LoadedRepositorySession repositorySession,
        ClassificationOutcome classification)
    {
        RepositorySession = repositorySession;
        Classification = classification;
    }

    public LoadedRepositorySession RepositorySession { get; }

    public ClassificationOutcome Classification { get; }
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
    string? RepositoryIdentity,
    GeneratedSourceFact? GeneratedSource);

internal sealed record GeneratedSourceBinding(
    SyntaxTree SyntaxTree,
    LoadedSourceKind Kind,
    GeneratedSourceFact Fact);
