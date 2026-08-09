using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using MsBuildProject = Microsoft.Build.Evaluation.Project;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace ContractScribe.Roslyn;

public sealed class RepositoryLoader
{
    private readonly RepositoryPathResolver pathResolver = new();
    private readonly Action<LoaderStage>? observer;
    private readonly Func<ReadOnlyMemory<byte>, byte[]> digest;
    private readonly Func<string, CancellationToken, IReadOnlyDictionary<string, InventoryEntry>> inventory;
    private readonly Action<int>? generatedAuthorityComparisonObserver;
    private readonly RegisteredToolchain? preselectedToolchain;

    public RepositoryLoader()
        : this(null, null)
    {
    }

    internal RepositoryLoader(
        Action<LoaderStage>? observer,
        Func<ReadOnlyMemory<byte>, byte[]>? digest = null,
        Func<string, CancellationToken, IReadOnlyDictionary<string, InventoryEntry>>? inventory = null,
        Action<int>? generatedAuthorityComparisonObserver = null,
        RegisteredToolchain? preselectedToolchain = null)
    {
        this.observer = observer;
        this.digest = digest ?? (bytes => SHA256.HashData(bytes.Span));
        this.inventory = inventory ?? RepositoryInventory.Capture;
        this.generatedAuthorityComparisonObserver =
            generatedAuthorityComparisonObserver;
        this.preselectedToolchain = preselectedToolchain;
    }

    public async Task<RepositoryLoadOutcome> LoadAsync(
        RepositoryLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ResolvedRepositoryPaths? paths = null;
        IReadOnlyDictionary<string, InventoryEntry>? before = null;
        PostRegistrationResult? loaded = null;
        RegisteredToolchain? selectedToolchain = null;
        LoaderExecutionState? state = null;
        RepositoryLoadOutcome outcome;
        try
        {
            Observe(LoaderStage.RequestValidation, cancellationToken);
            paths = pathResolver.Resolve(request.RepositoryRoot, request.InputPath);
            state = new LoaderExecutionState();
            state.AddProtected(pathResolver.RelativeIdentity(paths.PhysicalRoot, paths.PhysicalInput));
            state.AddProtected(pathResolver.RelativeIdentity(paths.LexicalRoot, paths.LexicalInput));
            state.AddPolicy(
                ReparseEntryIdentities(paths.PhysicalRoot, paths.TraversedReparseEntries, pathResolver),
                []);
            Observe(LoaderStage.PathResolution, cancellationToken);
            before = inventory(paths.PhysicalRoot, cancellationToken);
            Observe(LoaderStage.BaselineCapture, cancellationToken);
            selectedToolchain = preselectedToolchain
                ?? await MsBuildBootstrap.EnsureRegisteredAsync(
                    Path.GetDirectoryName(paths.PhysicalInput)!,
                    cancellationToken);
            Observe(LoaderStage.ToolchainRegistration, cancellationToken);
            loaded = await PostRegistrationLoader.LoadAsync(
                paths,
                selectedToolchain,
                pathResolver,
                request.ToolGeneratedSources ?? [],
                state,
                observer,
                digest,
                generatedAuthorityComparisonObserver,
                cancellationToken);
            outcome = RepositoryLoadOutcome.Success(loaded.Session, loaded.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Cancelled(
                selectedToolchain?.Identity,
                state?.Diagnostics);
        }
        catch (LoaderException exception)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Failure(
                new LoaderFact(exception.Stage, exception.Code),
                selectedToolchain?.Identity,
                state?.Diagnostics);
        }
        catch (Exception)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Failure(
                new LoaderFact("internal", "loader.internal-error"),
                selectedToolchain?.Identity,
                state?.Diagnostics);
        }

        if (paths is null || before is null)
        {
            return outcome;
        }

        try
        {
            var after = inventory(paths.PhysicalRoot, CancellationToken.None);
            var drift = RepositoryInventory.ChangedPaths(before, after)
                .Where(path => IsProtectedDrift(path, state))
                .ToArray();
            if (cancellationToken.IsCancellationRequested)
            {
                outcome.Session?.Dispose();
                var secondary = drift.Length == 0
                    ? Array.Empty<LoaderFact>()
                    : [new LoaderFact("repository", "repository.protected-drift")];
                return RepositoryLoadOutcome.Cancelled(
                    outcome.Toolchain,
                    outcome.Diagnostics,
                    secondary);
            }

            if (drift.Length == 0)
            {
                return outcome;
            }

            var fact = new LoaderFact("repository", "repository.protected-drift");
            if (outcome.Status == RepositoryLoadStatus.Success)
            {
                outcome.Session?.Dispose();
                return RepositoryLoadOutcome.Failure(
                    fact,
                    outcome.Toolchain,
                    outcome.Diagnostics,
                    outcome.SecondaryFacts);
            }

            return outcome.Status == RepositoryLoadStatus.Cancelled
                ? RepositoryLoadOutcome.Cancelled(outcome.Toolchain, outcome.Diagnostics, [fact])
                : RepositoryLoadOutcome.Failure(
                    outcome.PrimaryFailure ?? new LoaderFact("internal", "loader.internal-error"),
                    outcome.Toolchain,
                    diagnostics: outcome.Diagnostics,
                    secondaryFacts: outcome.SecondaryFacts.Concat([fact]).ToArray());
        }
        catch (Exception)
        {
            var fact = new LoaderFact("repository", "repository.drift-scan-failed");
            if (cancellationToken.IsCancellationRequested)
            {
                outcome.Session?.Dispose();
                return RepositoryLoadOutcome.Cancelled(
                    outcome.Toolchain,
                    outcome.Diagnostics,
                    outcome.SecondaryFacts.Concat([fact]).ToArray());
            }

            if (outcome.Status == RepositoryLoadStatus.Success)
            {
                outcome.Session?.Dispose();
                return RepositoryLoadOutcome.Failure(
                    fact,
                    outcome.Toolchain,
                    outcome.Diagnostics,
                    outcome.SecondaryFacts);
            }

            return outcome.Status == RepositoryLoadStatus.Cancelled
                ? RepositoryLoadOutcome.Cancelled(outcome.Toolchain, outcome.Diagnostics, [fact])
                : RepositoryLoadOutcome.Failure(
                    outcome.PrimaryFailure ?? new LoaderFact("internal", "loader.internal-error"),
                    outcome.Toolchain,
                    diagnostics: outcome.Diagnostics,
                    secondaryFacts: outcome.SecondaryFacts.Concat([fact]).ToArray());
        }
    }

    private void Observe(LoaderStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observer?.Invoke(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsProtectedDrift(string path, LoaderExecutionState? state)
    {
        if (state is null)
        {
            return true;
        }

        if (state.ProtectedPaths.Contains(path))
        {
            return true;
        }

        if (IsRepositoryInputExtension(path))
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return !state.AllowedOutputRoots.Any(root =>
            path.Equals(root, comparison)
            || path.StartsWith(root + "/", comparison));
    }

    private static bool IsRepositoryInputExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".editorconfig";

    private static IEnumerable<string> ReparseEntryIdentities(
        string physicalRoot,
        IEnumerable<string> entries,
        RepositoryPathResolver resolver) =>
        entries
            .Where(entry => IsContained(physicalRoot, entry))
            .Select(entry => resolver.RelativeIdentity(physicalRoot, entry));

    private static bool IsContained(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(rootPrefix, comparison);
    }
}

internal static class PostRegistrationLoader
{
    private static readonly IReadOnlyDictionary<string, string> PinnedBuildHostGlobalProperties =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DesignTimeBuild"] = "true",
            ["NonExistentFile"] = @"__NonExistentSubDir__\__NonExistentFile__",
            ["BuildingInsideVisualStudio"] = "true",
            ["BuildProjectReferences"] = "false",
            ["BuildingProject"] = "false",
            ["ProvideCommandLineArgs"] = "true",
            ["SkipCompilerExecution"] = "true",
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = "false",
        };

    public static async Task<PostRegistrationResult> LoadAsync(
        ResolvedRepositoryPaths paths,
        RegisteredToolchain toolchain,
        RepositoryPathResolver pathResolver,
        IReadOnlyList<ToolGeneratedSourceInput> toolGeneratedSources,
        LoaderExecutionState state,
        Action<LoaderStage>? observer,
        Func<ReadOnlyMemory<byte>, byte[]> digest,
        Action<int>? generatedAuthorityComparisonObserver,
        CancellationToken cancellationToken)
    {
        var identities = new GeneratedIdentityHasher(digest);
        VerifyExecutingMsbuild(toolchain);
        var evaluationProperties = CreateEvaluationProperties(paths);
        var roots = ResolveRoots(paths, pathResolver, state);
        Observe(observer, LoaderStage.InputParsing, cancellationToken);
        var graph = EvaluateGraph(
            paths,
            roots,
            evaluationProperties,
            pathResolver,
            state,
            cancellationToken);
        var graphIdentities = graph.Values.Select(node => node.Identity).ToHashSet(StringComparer.Ordinal);
        if (toolGeneratedSources.Any(input => !graphIdentities.Contains(input.ProjectIdentity)))
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        Observe(observer, LoaderStage.GraphEvaluation, cancellationToken);
        var workspace = MSBuildWorkspace.Create(
            new Dictionary<string, string>(evaluationProperties, StringComparer.Ordinal));
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.SkipUnrecognizedProjects = false;
        workspace.WorkspaceFailed += (_, args) =>
        {
            state.AddDiagnostic(new LoaderDiagnostic(
                "workspace",
                args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? "workspace.diagnostic-failure"
                    : "workspace.diagnostic-warning",
                args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "error" : "warning"));
        };

        try
        {
            Solution solution;
            if (paths.PhysicalInput.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(paths.PhysicalInput, cancellationToken: cancellationToken);
                solution = project.Solution;
            }
            else
            {
                solution = await workspace.OpenSolutionAsync(paths.PhysicalInput, cancellationToken: cancellationToken);
            }

            var workspaceProjects = solution.Projects.ToArray();
            ProtectWorkspaceSemanticInputs(
                paths,
                graph,
                workspaceProjects,
                toolchain,
                pathResolver,
                state);
            Observe(observer, LoaderStage.WorkspaceLoad, cancellationToken);
            if (workspaceProjects.Any(project => project.Language != LanguageNames.CSharp))
            {
                throw LoaderException.Workspace("workspace.non-csharp-project");
            }

            var byPath = workspaceProjects.ToDictionary(
                project => Path.GetFullPath(project.FilePath ?? string.Empty),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (!graph.Keys.ToHashSet(byPath.Comparer).SetEquals(byPath.Keys))
            {
                throw LoaderException.Workspace("workspace.project-graph-mismatch");
            }

            var loadedProjects = new List<LoadedProject>(graph.Count);
            var generatedFacts = new List<GeneratedSourceFact>();
            foreach (var node in graph.Values.OrderBy(node => node.Identity, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byPath.TryGetValue(node.Path, out var project))
                {
                    throw LoaderException.Workspace("workspace.project-graph-mismatch");
                }

                var actualReferences = project.ProjectReferences
                    .Select(reference => solution.GetProject(reference.ProjectId)?.FilePath)
                    .Where(path => path is not null)
                    .Select(path => Path.GetFullPath(path!))
                    .ToHashSet(byPath.Comparer);
                if (!node.References.ToHashSet(byPath.Comparer).SetEquals(actualReferences))
                {
                    throw LoaderException.Workspace("workspace.project-graph-mismatch");
                }

                var observedTfm = ObserveTargetFramework(project);
                if (!string.Equals(observedTfm, node.TargetFramework, StringComparison.Ordinal))
                {
                    throw LoaderException.Workspace("workspace.target-framework-mismatch");
                }

                var compilation = await project.GetCompilationAsync(cancellationToken)
                    ?? throw LoaderException.Compilation("compilation.unavailable");
                var workspaceSourceTrees = await ValidateWorkspaceSourcesAsync(
                    paths,
                    node,
                    project,
                    toolchain,
                    pathResolver,
                    state,
                    cancellationToken);
                var authoritativeGenerated =
                    new List<GeneratedAuthorityDocument>();
                var authoritativeGeneratedTrees = new List<SyntaxTree>();
                foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
                {
                    var tree = await document.GetSyntaxTreeAsync(cancellationToken)
                        ?? throw LoaderException.Generated("run.generated.authority-conflict");
                    authoritativeGeneratedTrees.Add(tree);
                    authoritativeGenerated.Add(new GeneratedAuthorityDocument(
                        document.Name,
                        document.FilePath,
                        (await document.GetTextAsync(cancellationToken)).ToString(),
                        tree));
                }

                var authoritativeCompilationTrees = new HashSet<SyntaxTree>(
                    workspaceSourceTrees.Keys,
                    ReferenceEqualityComparer.Instance);
                authoritativeCompilationTrees.UnionWith(authoritativeGeneratedTrees);
                if (!authoritativeCompilationTrees.SetEquals(compilation.SyntaxTrees))
                {
                    throw LoaderException.Graph("graph.source-outside-root");
                }

                if (compilation.GetDiagnostics(cancellationToken)
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    throw LoaderException.Compilation("compilation.errors");
                }

                Observe(observer, LoaderStage.Compilation, cancellationToken);
                var contextRef = ContextRef(node.Identity, node.TargetFramework, identities);
                var generatedAuthorityIndex =
                    new GeneratedAuthorityIndex(authoritativeGenerated);
                var sourceGeneratorBindings = RunGenerators(
                    project,
                    compilation.RemoveSyntaxTrees(authoritativeGeneratedTrees),
                    node.Identity,
                    contextRef,
                    generatedAuthorityIndex,
                    identities,
                    cancellationToken);
                generatedAuthorityComparisonObserver?.Invoke(
                    generatedAuthorityIndex.CandidateComparisons);
                if (generatedAuthorityIndex.UnmatchedCount != 0)
                {
                    throw LoaderException.Generated("run.generated.authority-conflict");
                }

                var toolBindings = CreateToolGeneratedFacts(
                    toolGeneratedSources.Where(input => input.ProjectIdentity == node.Identity).ToArray(),
                    node.Identity,
                    contextRef,
                    project.ParseOptions as CSharpParseOptions,
                    ref compilation,
                    identities,
                    cancellationToken);
                generatedFacts.AddRange(sourceGeneratorBindings.Select(binding => binding.Fact));
                generatedFacts.AddRange(toolBindings.Select(binding => binding.Fact));
                var sourceTrees = new Dictionary<SyntaxTree, LoadedSourceTree>(
                    ReferenceEqualityComparer.Instance);
                foreach (var pair in workspaceSourceTrees)
                {
                    sourceTrees.Add(
                        pair.Key,
                        new LoadedSourceTree(
                            LoadedSourceKind.Repository,
                            pair.Value,
                            null));
                }

                foreach (var binding in sourceGeneratorBindings.Concat(toolBindings))
                {
                    sourceTrees.Add(
                        binding.SyntaxTree,
                        new LoadedSourceTree(
                            binding.Kind,
                            null,
                            binding.Fact));
                }

                Observe(observer, LoaderStage.GeneratedFacts, cancellationToken);
                loadedProjects.Add(new LoadedProject(
                    node.Identity,
                    node.TargetFramework,
                    contextRef,
                    node.IsRoot ? LoadedProjectRole.AuditRoot : LoadedProjectRole.DependencyOnly,
                    node.References.Select(reference => graph[reference].Identity).Order(StringComparer.Ordinal).ToArray(),
                    project,
                    compilation,
                    sourceTrees));
            }

            if (state.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
            {
                throw LoaderException.Workspace("workspace.load-failed");
            }

            Observe(observer, LoaderStage.TerminalValidation, cancellationToken);
            var session = new LoadedRepositorySession(
                ".",
                pathResolver.RelativeIdentity(paths.PhysicalRoot, paths.PhysicalInput),
                toolchain.Identity,
                loadedProjects,
                ValidateGeneratedFacts(generatedFacts),
                workspace);
            return new PostRegistrationResult(
                session,
                state.Diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static void ProtectWorkspaceSemanticInputs(
        ResolvedRepositoryPaths paths,
        IReadOnlyDictionary<string, EvaluatedProject> graph,
        IReadOnlyList<RoslynProject> projects,
        RegisteredToolchain toolchain,
        RepositoryPathResolver resolver,
        LoaderExecutionState state)
    {
        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath)
                || !graph.TryGetValue(Path.GetFullPath(project.FilePath), out var node))
            {
                throw LoaderException.Workspace("workspace.project-graph-mismatch");
            }

            foreach (var document in project.Documents)
            {
                ProtectWorkspaceSemanticInput(
                    paths,
                    node,
                    toolchain,
                    document.FilePath,
                    resolver,
                    state);
            }

            foreach (var document in project.AdditionalDocuments.Concat(project.AnalyzerConfigDocuments))
            {
                ProtectWorkspaceSemanticInput(
                    paths,
                    node,
                    toolchain,
                    document.FilePath,
                    resolver,
                    state);
            }
        }
    }

    private static void Observe(
        Action<LoaderStage>? observer,
        LoaderStage stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observer?.Invoke(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static IReadOnlyDictionary<string, string> CreateEvaluationProperties(
        ResolvedRepositoryPaths paths)
    {
        var properties = new Dictionary<string, string>(
            PinnedBuildHostGlobalProperties,
            StringComparer.Ordinal);
        if (!paths.PhysicalInput.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedSolutionDirectory =
                Path.TrimEndingDirectorySeparator(
                    Path.GetDirectoryName(paths.PhysicalInput)!);
            properties["SolutionDir"] =
                Path.EndsInDirectorySeparator(normalizedSolutionDirectory)
                    ? normalizedSolutionDirectory
                    : normalizedSolutionDirectory + Path.DirectorySeparatorChar;
        }

        return properties;
    }

    private static IReadOnlyList<string> ResolveRoots(
        ResolvedRepositoryPaths paths,
        RepositoryPathResolver pathResolver,
        LoaderExecutionState state)
    {
        if (paths.PhysicalInput.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var directProject = pathResolver.ResolveProject(
                paths.LexicalRoot,
                paths.PhysicalRoot,
                paths.LexicalInput);
            state.AddPolicy(ProtectionIdentities(paths, directProject, pathResolver), []);
            return [directProject.PhysicalPath];
        }

        var parsed = SolutionFile.Parse(paths.PhysicalInput);
        var listed = parsed.ProjectsInOrder
            .Where(project => project.ProjectType != SolutionProjectType.SolutionFolder)
            .ToArray();
        if (listed.Length == 0
            || listed.Any(project => !project.AbsolutePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            throw LoaderException.Graph("graph.solution-not-all-csharp");
        }

        var resolved = listed
            .Select(project => pathResolver.ResolveProject(paths.LexicalRoot, paths.PhysicalRoot, project.AbsolutePath))
            .ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (resolved.Select(project => project.PhysicalPath).Distinct(comparer).Count() != resolved.Length)
        {
            throw LoaderException.Graph("graph.duplicate-project");
        }

        state.AddPolicy(
            resolved.SelectMany(project => ProtectionIdentities(paths, project, pathResolver)),
            []);
        return resolved
            .Select(project => project.PhysicalPath)
            .OrderBy(path => pathResolver.RelativeIdentity(paths.PhysicalRoot, path), StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, EvaluatedProject> EvaluateGraph(
        ResolvedRepositoryPaths paths,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, string> evaluationProperties,
        RepositoryPathResolver pathResolver,
        LoaderExecutionState state,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var graph = new Dictionary<string, EvaluatedProject>(comparer);
        var rootSet = roots.ToHashSet(comparer);
        using var collection = new ProjectCollection();
        var pending = new Stack<string>(roots.Reverse());
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = pending.Pop();
            if (graph.ContainsKey(projectPath))
            {
                continue;
            }

            MsBuildProject discovery;
            try
            {
                discovery = collection.LoadProject(
                    projectPath,
                    new Dictionary<string, string>(evaluationProperties, StringComparer.Ordinal),
                    toolsVersion: null);
            }
            catch (InvalidProjectFileException)
            {
                throw LoaderException.Graph("graph.project-unloadable");
            }
            var targetFrameworkProperty = discovery.GetProperty("TargetFramework");
            var targetFramework = targetFrameworkProperty?.EvaluatedValue.Trim() ?? string.Empty;
            var targetFrameworks = discovery.GetPropertyValue("TargetFrameworks").Trim();
            if (string.IsNullOrWhiteSpace(targetFramework)
                || !string.IsNullOrWhiteSpace(targetFrameworks)
                || targetFramework.Contains(';', StringComparison.Ordinal)
                || targetFrameworkProperty!.IsEnvironmentProperty
                || targetFrameworkProperty.IsGlobalProperty)
            {
                collection.UnloadProject(discovery);
                throw LoaderException.Graph("graph.target-framework-not-single");
            }

            collection.UnloadProject(discovery);
            MsBuildProject project;
            try
            {
                var globalProperties = new Dictionary<string, string>(
                    evaluationProperties,
                    StringComparer.Ordinal)
                {
                    ["TargetFramework"] = targetFramework,
                };
                project = collection.LoadProject(
                    projectPath,
                    globalProperties,
                    toolsVersion: null);
            }
            catch (InvalidProjectFileException)
            {
                throw LoaderException.Graph("graph.project-unloadable");
            }
            var referenceResolutions = project.GetItems("ProjectReference")
                .Select(item => item.GetMetadataValue("FullPath"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => pathResolver.ResolveProject(paths.LexicalRoot, paths.PhysicalRoot, path))
                .ToArray();
            var references = referenceResolutions
                .Select(reference => reference.PhysicalPath)
                .Order(comparer)
                .ToArray();
            if (references.Distinct(comparer).Count() != references.Length)
            {
                collection.UnloadProject(project);
                throw LoaderException.Graph("graph.duplicate-project");
            }

            var assets = project.GetPropertyValue("ProjectAssetsFile");
            if (string.IsNullOrWhiteSpace(assets) || !File.Exists(assets))
            {
                collection.UnloadProject(project);
                throw LoaderException.Graph("graph.restore-assets-missing");
            }

            RequireSourceInputsContained(paths.PhysicalRoot, project);
            var protectedPaths = ProtectedPaths(paths, projectPath, project, pathResolver);
            var allowedRoots = AllowedOutputRoots(paths.PhysicalRoot, project, pathResolver);
            var allowedExternalSemanticRoots = AllowedExternalSemanticRoots(paths.PhysicalRoot, project);
            state.AddPolicy(
                protectedPaths.Concat(referenceResolutions.SelectMany(
                    reference => ProtectionIdentities(paths, reference, pathResolver))),
                allowedRoots);
            var identity = pathResolver.RelativeIdentity(paths.PhysicalRoot, projectPath);
            graph[projectPath] = new EvaluatedProject(
                projectPath,
                identity,
                targetFramework.Normalize(),
                rootSet.Contains(projectPath),
                references,
                protectedPaths,
                allowedRoots,
                allowedExternalSemanticRoots);
            collection.UnloadProject(project);
            foreach (var reference in references.Reverse())
            {
                pending.Push(reference);
            }
        }

        return graph;
    }

    private static async Task<IReadOnlyDictionary<SyntaxTree, string>> ValidateWorkspaceSourcesAsync(
        ResolvedRepositoryPaths paths,
        EvaluatedProject node,
        RoslynProject project,
        RegisteredToolchain toolchain,
        RepositoryPathResolver resolver,
        LoaderExecutionState state,
        CancellationToken cancellationToken)
    {
        var trees = new Dictionary<SyntaxTree, string>(ReferenceEqualityComparer.Instance);
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = document.FilePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw LoaderException.Graph("graph.source-outside-root");
            }

            var resolution = resolver.ResolveSource(paths.PhysicalRoot, sourcePath);
            state.AddPolicy(
                ProtectionIdentities(paths, resolution, resolver)
                    .Append(resolver.RelativeIdentity(paths.PhysicalRoot, Path.GetFullPath(sourcePath))),
                []);
            var tree = await document.GetSyntaxTreeAsync(cancellationToken)
                ?? throw LoaderException.Graph("graph.source-outside-root");
            trees.Add(
                tree,
                resolver.RelativeIdentity(paths.PhysicalRoot, Path.GetFullPath(sourcePath)));
        }

        foreach (var document in project.AdditionalDocuments.Concat(project.AnalyzerConfigDocuments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtectWorkspaceSemanticInput(
                paths,
                node,
                toolchain,
                document.FilePath,
                resolver,
                state);
        }

        return trees;
    }

    private static void ProtectWorkspaceSemanticInput(
        ResolvedRepositoryPaths paths,
        EvaluatedProject node,
        RegisteredToolchain toolchain,
        string? documentPath,
        RepositoryPathResolver resolver,
        LoaderExecutionState state)
    {
        if (string.IsNullOrWhiteSpace(documentPath)
            || !Path.IsPathFullyQualified(documentPath))
        {
            throw LoaderException.Graph("graph.source-outside-root");
        }

        var fullPath = Path.GetFullPath(documentPath);
        if (IsContained(paths.PhysicalRoot, fullPath))
        {
            var resolution = resolver.ResolveSource(paths.PhysicalRoot, fullPath);
            state.AddPolicy(
                ProtectionIdentities(paths, resolution, resolver)
                    .Append(resolver.RelativeIdentity(paths.PhysicalRoot, fullPath)),
                []);
            return;
        }

        var externalRoot = node.AllowedExternalSemanticRoots
            .Append(toolchain.MsbuildPath)
            .FirstOrDefault(root => IsContained(root, fullPath));
        if (externalRoot is null)
        {
            throw LoaderException.Graph("graph.source-outside-root");
        }

        _ = resolver.ResolveSemantic(externalRoot, fullPath);
    }

    private static void RequireSourceInputsContained(
        string physicalRoot,
        MsBuildProject project)
    {
        foreach (var item in project.GetItems("Compile"))
        {
            var path = item.GetMetadataValue("FullPath");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            if (!IsContained(physicalRoot, path))
            {
                throw LoaderException.Graph("graph.source-outside-root");
            }
        }
    }

    private static IReadOnlyList<string> ProtectedPaths(
        ResolvedRepositoryPaths paths,
        string projectPath,
        MsBuildProject project,
        RepositoryPathResolver resolver)
    {
        var semanticPaths = new[] { projectPath, paths.PhysicalInput, paths.LexicalInput }
            .Concat(project.Imports.Select(import => import.ImportedProject.FullPath))
            .Concat(new[] { "Compile", "AdditionalFiles", "Analyzer", "AnalyzerConfigFiles", "EditorConfigFiles" }
                .SelectMany(itemType => project.GetItems(itemType).Select(item => item.GetMetadataValue("FullPath"))))
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(path => Path.GetFullPath(path))
            .Where(path => IsContained(paths.PhysicalRoot, path))
            .SelectMany(path => ProtectionIdentities(
                paths,
                resolver.ResolveSemantic(paths.PhysicalRoot, path),
                resolver).Append(resolver.RelativeIdentity(paths.PhysicalRoot, path)))
            .Distinct(PathComparer())
            .Order(StringComparer.Ordinal)
            .ToArray();
        return semanticPaths;
    }

    private static IEnumerable<string> ProtectionIdentities(
        ResolvedRepositoryPaths paths,
        ResolvedPhysicalPath resolution,
        RepositoryPathResolver resolver) =>
        resolution.TraversedReparseEntries
            .Append(resolution.PhysicalPath)
            .Where(path => IsContained(paths.PhysicalRoot, path))
            .Select(path => resolver.RelativeIdentity(paths.PhysicalRoot, path));

    private static IReadOnlyList<string> AllowedOutputRoots(
        string root,
        MsBuildProject project,
        RepositoryPathResolver resolver)
    {
        var directory = Path.GetDirectoryName(project.FullPath)!;
        return new[]
            {
                "MSBuildProjectExtensionsPath",
                "BaseIntermediateOutputPath",
                "IntermediateOutputPath",
                "BaseOutputPath",
                "OutputPath",
            }
            .Select(name => project.GetPropertyValue(name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(directory, value)))
            .Where(path => IsContained(root, path))
            .Select(path => resolver.RelativeIdentity(root, path).TrimEnd('/'))
            .Distinct(PathComparer())
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> AllowedExternalSemanticRoots(
        string repositoryRoot,
        MsBuildProject project)
    {
        var projectDirectory = Path.GetDirectoryName(project.FullPath)!;
        return new[]
            {
                "NuGetPackageRoot",
                "NuGetPackageFolders",
                "MSBuildToolsPath",
                "MSBuildSDKsPath",
                "RoslynTargetsPath",
                "NETCoreSdkDir",
            }
            .SelectMany(name => project.GetPropertyValue(name)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => TryGetFullPath(projectDirectory, value))
            .Where(path => path is not null
                && Directory.Exists(path)
                && !IsContained(repositoryRoot, path))
            .Select(path => path!)
            .Distinct(PathComparer())
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? TryGetFullPath(string baseDirectory, string path)
    {
        try
        {
            return Path.GetFullPath(
                Path.IsPathFullyQualified(path)
                    ? path
                    : Path.Combine(baseDirectory, path));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }

    private static string ObserveTargetFramework(RoslynProject project)
    {
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.TargetFramework",
                out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value.Normalize();
        }

        var symbol = (project.ParseOptions as CSharpParseOptions)?.PreprocessorSymbolNames
            .FirstOrDefault(candidate => candidate.StartsWith("NET", StringComparison.Ordinal)
                && candidate.EndsWith("_OR_GREATER", StringComparison.Ordinal));
        if (symbol is null)
        {
            throw LoaderException.Workspace("workspace.target-framework-unavailable");
        }

        var version = symbol[3..^11].Replace('_', '.');
        return $"net{version}".ToLowerInvariant();
    }

    private static IReadOnlyList<GeneratedSourceBinding> RunGenerators(
        RoslynProject project,
        Compilation compilation,
        string projectIdentity,
        string contextRef,
        GeneratedAuthorityIndex authoritativeGenerated,
        GeneratedIdentityHasher identities,
        CancellationToken cancellationToken)
    {
        var generators = project.AnalyzerReferences
            .SelectMany(reference => reference.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();
        if (generators.IsDefaultOrEmpty)
        {
            return [];
        }

        var parseOptions = project.ParseOptions as CSharpParseOptions
            ?? throw LoaderException.Generated("run.generated.missing-identity");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators,
            project.AnalyzerOptions.AdditionalFiles,
            parseOptions,
            project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        driver = driver.RunGenerators(compilation, cancellationToken);
        var bindings = new List<GeneratedSourceBinding>();
        foreach (var result in driver.GetRunResult().Results)
        {
            if (result.Exception is not null)
            {
                throw LoaderException.Generated("run.generated.authority-conflict");
            }

            var type = result.Generator.GetGeneratorType();
            var producerType = type.FullName;
            var assemblyName = type.Assembly.GetName();
            if (string.IsNullOrWhiteSpace(producerType) || string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                throw LoaderException.Generated("run.generated.missing-identity");
            }

            var producerAssembly = new AssemblyIdentity(
                assemblyName.Name,
                assemblyName.Version,
                assemblyName.CultureName,
                assemblyName.GetPublicKeyToken().ToImmutableArray(),
                hasPublicKey: false)
                .GetDisplayName(fullKey: false);
            var producerId = "sgp." + identities.Hash("contract-scribe/sgp/v1", producerType, producerAssembly);
            foreach (var source in result.GeneratedSources)
            {
                var text = source.SourceText.ToString();
                var sourceBytes = StrictUtf8(text);
                var authority = authoritativeGenerated.Match(
                    source.HintName,
                    source.SyntaxTree.FilePath,
                    text,
                    sourceBytes);

                bindings.Add(new GeneratedSourceBinding(
                    authority.Tree,
                    LoadedSourceKind.SourceGenerator,
                    new GeneratedSourceFact(
                        projectIdentity,
                        contextRef,
                        producerId,
                        "sgo." + identities.Hash("contract-scribe/sgo/v1", source.HintName),
                        Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
                        text)));
            }
        }

        return bindings;
    }

    private static IReadOnlyList<GeneratedSourceBinding> CreateToolGeneratedFacts(
        IReadOnlyList<ToolGeneratedSourceInput> inputs,
        string projectIdentity,
        string contextRef,
        CSharpParseOptions? parseOptions,
        ref Compilation compilation,
        GeneratedIdentityHasher identities,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        if (parseOptions is null)
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        var grammar = new System.Text.RegularExpressions.Regex(
            @"\A[A-Za-z][A-Za-z0-9._-]{0,127}\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var normalizedInputs = inputs
            .Select(input => input with
            {
                ProducerNamespace = NormalizeToolIdentityField(input.ProducerNamespace, grammar),
                ProducerName = NormalizeToolIdentityField(input.ProducerName, grammar),
                OutputName = NormalizeToolIdentityField(input.OutputName, grammar),
            })
            .GroupBy(
                input => (input.ProjectIdentity, input.ProducerNamespace, input.ProducerName, input.OutputName))
            .Select(group =>
            {
                if (group.Select(input => input.SourceText).Distinct(StringComparer.Ordinal).Skip(1).Any())
                {
                    throw LoaderException.Generated("run.generated.authority-conflict");
                }

                return group.First();
            })
            .ToArray();
        var bindings = new List<GeneratedSourceBinding>(normalizedInputs.Length);
        foreach (var input in normalizedInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ProjectIdentity != projectIdentity
                || string.IsNullOrWhiteSpace(input.ProjectIdentity))
            {
                throw LoaderException.Generated("run.generated.missing-identity");
            }

            var sourceBytes = StrictUtf8(input.SourceText);
            var outputId = "tgo." + identities.Hash("contract-scribe/tgo/v1", input.OutputName);
            var tree = CSharpSyntaxTree.ParseText(
                input.SourceText,
                parseOptions,
                path: $"tool-generated://{outputId}",
                cancellationToken: cancellationToken);
            compilation = compilation.AddSyntaxTrees(tree);
            if (tree.GetDiagnostics(cancellationToken)
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                || compilation.GetDiagnostics(cancellationToken)
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                throw LoaderException.Generated("run.generated.authority-conflict");
            }

            bindings.Add(new GeneratedSourceBinding(
                tree,
                LoadedSourceKind.ToolGenerated,
                new GeneratedSourceFact(
                    projectIdentity,
                    contextRef,
                    "tgp." + identities.Hash(
                        "contract-scribe/tgp/v1",
                        input.ProducerNamespace,
                        input.ProducerName),
                    outputId,
                    Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
                    input.SourceText)));
        }

        return bindings;
    }

    private static string NormalizeToolIdentityField(
        string? value,
        System.Text.RegularExpressions.Regex grammar)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        var bytes = StrictUtf8(normalized);
        if (bytes.Length == 0 || bytes.Length > 4096 || !grammar.IsMatch(normalized))
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        return normalized;
    }

    private static byte[] StrictUtf8(string value)
    {
        try
        {
            return new UTF8Encoding(false, true).GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }
    }

    private static IReadOnlyList<GeneratedSourceFact> ValidateGeneratedFacts(
        IReadOnlyList<GeneratedSourceFact> facts)
    {
        foreach (var group in facts.GroupBy(fact => (fact.ProjectIdentity, fact.CompilationContextRef, fact.ProducerId, fact.OutputId)))
        {
            var distinct = group.Select(fact => fact.SourceSha256).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length > 1)
            {
                throw LoaderException.Generated("run.generated.authority-conflict");
            }
        }

        return facts
            .DistinctBy(fact => (fact.ProjectIdentity, fact.CompilationContextRef, fact.ProducerId, fact.OutputId, fact.SourceSha256))
            .OrderBy(fact => fact.ProjectIdentity, StringComparer.Ordinal)
            .ThenBy(fact => fact.ProducerId, StringComparer.Ordinal)
            .ThenBy(fact => fact.OutputId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ContextRef(
        string projectIdentity,
        string targetFramework,
        GeneratedIdentityHasher identities) =>
        "ctx-" + identities.Hash("contract-scribe/compilation-context/v1", projectIdentity, targetFramework);

    private static void VerifyExecutingMsbuild(RegisteredToolchain toolchain)
    {
        var assemblyPath = Path.GetFullPath(typeof(MsBuildProject).Assembly.Location);
        var expectedPrefix = Path.TrimEndingDirectorySeparator(toolchain.MsbuildPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!assemblyPath.StartsWith(expectedPrefix, comparison))
        {
            throw LoaderException.Toolchain("toolchain.assembly-mismatch");
        }
    }

    private static bool IsContained(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(rootPrefix, comparison);
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record GeneratedAuthorityDocument(
    string Name,
    string? FilePath,
    string Text,
    SyntaxTree Tree);

internal sealed class GeneratedAuthorityIndex
{
    private static readonly UTF8Encoding StrictUtf8Encoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IReadOnlyList<Entry> entries;
    private readonly Dictionary<
        (string Name, string Digest, string? GeneratorRelativePath),
        List<Entry>> buckets;

    public GeneratedAuthorityIndex(
        IReadOnlyList<GeneratedAuthorityDocument> documents)
    {
        entries = documents.Select(document => new Entry(
            document,
            NormalizePath(document.FilePath),
            CanonicalGeneratorRelativePath(
                document.FilePath,
                document.Name),
            Digest(document.Text))).ToArray();
        buckets = entries
            .GroupBy(entry => (
                entry.Document.Name,
                entry.ContentDigest,
                entry.GeneratorRelativePath))
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    public int CandidateComparisons { get; private set; }

    public int UnmatchedCount => entries.Count(entry => !entry.Matched);

    public GeneratedAuthorityDocument Match(
        string hintName,
        string? driverPath,
        string text,
        ReadOnlySpan<byte> utf8Text)
    {
        var key = (
            hintName,
            Convert.ToHexString(SHA256.HashData(utf8Text)).ToLowerInvariant(),
            CanonicalGeneratorRelativePath(driverPath, hintName));
        if (!buckets.TryGetValue(key, out var bucket))
        {
            throw LoaderException.Generated("run.generated.authority-conflict");
        }

        var normalizedDriverPath = NormalizePath(driverPath);
        Entry? match = null;
        foreach (var candidate in bucket)
        {
            CandidateComparisons++;
            if (candidate.Matched
                || !PathsMatch(candidate.NormalizedPath, normalizedDriverPath)
                || !string.Equals(
                    candidate.Document.Text,
                    text,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw LoaderException.Generated(
                    "run.generated.authority-conflict");
            }

            match = candidate;
        }

        if (match is null)
        {
            throw LoaderException.Generated("run.generated.authority-conflict");
        }

        match.Matched = true;
        return match.Document;
    }

    private static string Digest(string text)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(
                StrictUtf8Encoding.GetBytes(text))).ToLowerInvariant();
        }
        catch (EncoderFallbackException)
        {
            throw LoaderException.Generated("run.generated.authority-conflict");
        }
    }

    private static string? NormalizePath(string? path) =>
        path?.Replace('\\', '/');

    private static string? CanonicalGeneratorRelativePath(
        string? path,
        string hintName)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedHint = NormalizePath(hintName)?.TrimStart('/');
        if (normalizedPath is null
            || string.IsNullOrEmpty(normalizedHint))
        {
            return null;
        }

        if (string.Equals(
            normalizedPath.TrimStart('/'),
            normalizedHint,
            StringComparison.Ordinal))
        {
            return normalizedHint;
        }

        var suffix = "/" + normalizedHint;
        if (!normalizedPath.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = normalizedPath[..^suffix.Length].TrimEnd('/');
        var segments = prefix.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 2
            ? normalizedPath.TrimStart('/')
            : segments[^2] + "/" + segments[^1] + "/" + normalizedHint;
    }

    private static bool PathsMatch(
        string? normalizedWorkspace,
        string? normalizedDriver) =>
        normalizedWorkspace is not null
        && normalizedDriver is not null
        && (string.Equals(
                normalizedWorkspace,
                normalizedDriver,
                StringComparison.Ordinal)
            || normalizedWorkspace.EndsWith(
                "/" + normalizedDriver.TrimStart('/'),
                StringComparison.Ordinal));

    private sealed class Entry(
        GeneratedAuthorityDocument document,
        string? normalizedPath,
        string? generatorRelativePath,
        string contentDigest)
    {
        public GeneratedAuthorityDocument Document { get; } = document;

        public string? NormalizedPath { get; } = normalizedPath;

        public string? GeneratorRelativePath { get; } =
            generatorRelativePath;

        public string ContentDigest { get; } = contentDigest;

        public bool Matched { get; set; }
    }
}

internal sealed record EvaluatedProject(
    string Path,
    string Identity,
    string TargetFramework,
    bool IsRoot,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ProtectedPaths,
    IReadOnlyList<string> AllowedOutputRoots,
    IReadOnlyList<string> AllowedExternalSemanticRoots);

internal sealed record PostRegistrationResult(
    LoadedRepositorySession Session,
    IReadOnlyList<LoaderDiagnostic> Diagnostics);

internal sealed class LoaderExecutionState
{
    private readonly object gate = new();
    private readonly List<LoaderDiagnostic> diagnostics = [];
    private readonly HashSet<string> protectedPaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> allowedOutputRoots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyList<LoaderDiagnostic> Diagnostics
    {
        get
        {
            lock (gate)
            {
                return diagnostics
                    .Distinct()
                    .OrderBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .Take(32)
                    .ToArray();
            }
        }
    }

    public IReadOnlySet<string> ProtectedPaths
    {
        get
        {
            lock (gate)
            {
                return protectedPaths.ToHashSet(protectedPaths.Comparer);
            }
        }
    }

    public IReadOnlySet<string> AllowedOutputRoots
    {
        get
        {
            lock (gate)
            {
                return allowedOutputRoots.ToHashSet(allowedOutputRoots.Comparer);
            }
        }
    }

    public void AddDiagnostic(LoaderDiagnostic diagnostic)
    {
        lock (gate)
        {
            diagnostics.Add(diagnostic);
        }
    }

    public void AddProtected(string path)
    {
        lock (gate)
        {
            protectedPaths.Add(path);
        }
    }

    public void AddPolicy(
        IEnumerable<string> protectedMembers,
        IEnumerable<string> allowedRoots)
    {
        lock (gate)
        {
            protectedPaths.UnionWith(protectedMembers);
            allowedOutputRoots.UnionWith(allowedRoots);
        }
    }
}

internal enum LoaderStage
{
    RequestValidation,
    PathResolution,
    BaselineCapture,
    ToolchainRegistration,
    InputParsing,
    GraphEvaluation,
    WorkspaceLoad,
    Compilation,
    GeneratedFacts,
    TerminalValidation,
}

internal sealed class GeneratedIdentityHasher
{
    private readonly Func<ReadOnlyMemory<byte>, byte[]> digest;
    private readonly Dictionary<(string Domain, string Digest), byte[]> registrations = new();

    public GeneratedIdentityHasher(Func<ReadOnlyMemory<byte>, byte[]> digest)
    {
        this.digest = digest;
    }

    public string Hash(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(domain));
        stream.WriteByte(0);
        Span<byte> length = stackalloc byte[4];
        foreach (var raw in fields)
        {
            byte[] bytes;
            try
            {
                bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetBytes(raw.Normalize());
            }
            catch (Exception exception) when (
                exception is ArgumentException or EncoderFallbackException)
            {
                throw LoaderException.Generated("run.generated.missing-identity");
            }

            if (bytes.Length == 0 || bytes.Length > 4096)
            {
                throw LoaderException.Generated("run.generated.missing-identity");
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        var preimage = stream.ToArray();
        var hash = digest(preimage);
        if (hash.Length != 32)
        {
            throw LoaderException.Generated("run.generated.missing-identity");
        }

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var key = (domain, hex);
        if (registrations.TryGetValue(key, out var registered)
            && !registered.AsSpan().SequenceEqual(preimage))
        {
            throw LoaderException.Generated("run.generated.identity-collision");
        }

        registrations[key] = preimage;
        return hex;
    }
}
