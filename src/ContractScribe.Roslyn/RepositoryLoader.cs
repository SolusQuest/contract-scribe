using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
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

    public async Task<RepositoryLoadOutcome> LoadAsync(
        RepositoryLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ResolvedRepositoryPaths? paths = null;
        IReadOnlyDictionary<string, InventoryEntry>? before = null;
        PostRegistrationResult? loaded = null;
        RepositoryLoadOutcome outcome;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths = pathResolver.Resolve(request.RepositoryRoot, request.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            before = RepositoryInventory.Capture(paths.PhysicalRoot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var toolchain = await MsBuildBootstrap.EnsureRegisteredAsync(
                Path.GetDirectoryName(paths.PhysicalInput)!,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            loaded = await PostRegistrationLoader.LoadAsync(
                paths,
                toolchain,
                pathResolver,
                request.ToolGeneratedSources ?? [],
                cancellationToken);
            outcome = RepositoryLoadOutcome.Success(loaded.Session, loaded.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Cancelled();
        }
        catch (LoaderException exception)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Failure(new LoaderFact(exception.Stage, exception.Code));
        }
        catch (Exception)
        {
            loaded?.Session.Dispose();
            outcome = RepositoryLoadOutcome.Failure(new LoaderFact("internal", "loader.internal-error"));
        }

        if (paths is null || before is null)
        {
            return outcome;
        }

        try
        {
            var after = RepositoryInventory.Capture(paths.PhysicalRoot, CancellationToken.None);
            var drift = RepositoryInventory.ChangedPaths(before, after)
                .Where(path => IsProtectedDrift(path, loaded))
                .ToArray();
            if (drift.Length == 0)
            {
                return outcome;
            }

            var fact = new LoaderFact("repository", "repository.protected-drift");
            if (outcome.Status == RepositoryLoadStatus.Success)
            {
                outcome.Session?.Dispose();
                return RepositoryLoadOutcome.Failure(fact);
            }

            return outcome.Status == RepositoryLoadStatus.Cancelled
                ? RepositoryLoadOutcome.Cancelled([fact])
                : RepositoryLoadOutcome.Failure(
                    outcome.PrimaryFailure ?? new LoaderFact("internal", "loader.internal-error"),
                    outcome.Diagnostics,
                    outcome.SecondaryFacts.Concat([fact]).ToArray());
        }
        catch (Exception)
        {
            var fact = new LoaderFact("repository", "repository.drift-scan-failed");
            if (outcome.Status == RepositoryLoadStatus.Success)
            {
                outcome.Session?.Dispose();
                return RepositoryLoadOutcome.Failure(fact);
            }

            return outcome.Status == RepositoryLoadStatus.Cancelled
                ? RepositoryLoadOutcome.Cancelled([fact])
                : RepositoryLoadOutcome.Failure(
                    outcome.PrimaryFailure ?? new LoaderFact("internal", "loader.internal-error"),
                    outcome.Diagnostics,
                    outcome.SecondaryFacts.Concat([fact]).ToArray());
        }
    }

    private static bool IsProtectedDrift(string path, PostRegistrationResult? loaded)
    {
        if (loaded is null)
        {
            return true;
        }

        if (loaded.ProtectedPaths.Contains(path))
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return !loaded.AllowedOutputRoots.Any(root =>
            path.Equals(root, comparison)
            || path.StartsWith(root + "/", comparison));
    }
}

internal static class PostRegistrationLoader
{
    public static async Task<PostRegistrationResult> LoadAsync(
        ResolvedRepositoryPaths paths,
        RegisteredToolchain toolchain,
        RepositoryPathResolver pathResolver,
        IReadOnlyList<ToolGeneratedSourceInput> toolGeneratedSources,
        CancellationToken cancellationToken)
    {
        VerifyExecutingMsbuild(toolchain);
        var roots = ResolveRoots(paths, pathResolver);
        cancellationToken.ThrowIfCancellationRequested();
        var graph = EvaluateGraph(paths, roots, pathResolver, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<LoaderDiagnostic>();
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BuildingInsideVisualStudio"] = "true",
        });
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.SkipUnrecognizedProjects = false;
        workspace.WorkspaceFailed += (_, args) =>
        {
            diagnostics.Add(new LoaderDiagnostic(
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

            cancellationToken.ThrowIfCancellationRequested();
            var workspaceProjects = solution.Projects.ToArray();
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
                if (compilation.GetDiagnostics(cancellationToken)
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    throw LoaderException.Compilation("compilation.errors");
                }

                var contextRef = ContextRef(node.Identity, node.TargetFramework);
                generatedFacts.AddRange(RunGenerators(project, compilation, node.Identity, contextRef, cancellationToken));
                loadedProjects.Add(new LoadedProject(
                    node.Identity,
                    node.TargetFramework,
                    contextRef,
                    node.IsRoot ? LoadedProjectRole.AuditRoot : LoadedProjectRole.DependencyOnly,
                    node.References.Select(reference => graph[reference].Identity).Order(StringComparer.Ordinal).ToArray(),
                    project,
                    compilation));
            }

            if (diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
            {
                throw LoaderException.Workspace("workspace.load-failed");
            }

            generatedFacts.AddRange(CreateToolGeneratedFacts(toolGeneratedSources, loadedProjects));
            var session = new LoadedRepositorySession(
                ".",
                pathResolver.RelativeIdentity(paths.PhysicalRoot, paths.PhysicalInput),
                toolchain.Identity,
                loadedProjects,
                ValidateGeneratedFacts(generatedFacts),
                workspace);
            return new PostRegistrationResult(
                session,
                BoundDiagnostics(diagnostics),
                graph.Values.SelectMany(node => node.ProtectedPaths).ToHashSet(PathComparer()),
                graph.Values.SelectMany(node => node.AllowedOutputRoots).ToHashSet(PathComparer()));
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<string> ResolveRoots(
        ResolvedRepositoryPaths paths,
        RepositoryPathResolver pathResolver)
    {
        if (paths.PhysicalInput.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return [pathResolver.ResolveProject(paths.LexicalRoot, paths.PhysicalRoot, paths.LexicalInput)];
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
        if (resolved.Distinct(comparer).Count() != resolved.Length)
        {
            throw LoaderException.Graph("graph.duplicate-project");
        }

        return resolved;
    }

    private static Dictionary<string, EvaluatedProject> EvaluateGraph(
        ResolvedRepositoryPaths paths,
        IReadOnlyList<string> roots,
        RepositoryPathResolver pathResolver,
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

            var discovery = collection.LoadProject(projectPath);
            var targetFramework = discovery.GetPropertyValue("TargetFramework").Trim();
            var targetFrameworks = discovery.GetPropertyValue("TargetFrameworks").Trim();
            if (string.IsNullOrWhiteSpace(targetFramework)
                || !string.IsNullOrWhiteSpace(targetFrameworks)
                || targetFramework.Contains(';', StringComparison.Ordinal))
            {
                collection.UnloadProject(discovery);
                throw LoaderException.Graph("graph.target-framework-not-single");
            }

            collection.UnloadProject(discovery);
            var project = collection.LoadProject(
                projectPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TargetFramework"] = targetFramework,
                    ["BuildingInsideVisualStudio"] = "true",
                },
                toolsVersion: null);
            var references = project.GetItems("ProjectReference")
                .Select(item => item.GetMetadataValue("FullPath"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => pathResolver.ResolveProject(paths.LexicalRoot, paths.PhysicalRoot, path))
                .Order(comparer)
                .ToArray();
            var assets = project.GetPropertyValue("ProjectAssetsFile");
            if (string.IsNullOrWhiteSpace(assets) || !File.Exists(assets))
            {
                collection.UnloadProject(project);
                throw LoaderException.Graph("graph.restore-assets-missing");
            }

            var protectedPaths = ProtectedPaths(paths.PhysicalRoot, projectPath, project, pathResolver);
            var allowedRoots = AllowedOutputRoots(paths.PhysicalRoot, project, pathResolver);
            var identity = pathResolver.RelativeIdentity(paths.PhysicalRoot, projectPath);
            graph[projectPath] = new EvaluatedProject(
                projectPath,
                identity,
                targetFramework.Normalize(),
                rootSet.Contains(projectPath),
                references,
                protectedPaths,
                allowedRoots);
            collection.UnloadProject(project);
            foreach (var reference in references.Reverse())
            {
                pending.Push(reference);
            }
        }

        return graph;
    }

    private static IReadOnlyList<string> ProtectedPaths(
        string root,
        string projectPath,
        MsBuildProject project,
        RepositoryPathResolver resolver)
    {
        var paths = new[] { projectPath }
            .Concat(project.Imports.Select(import => import.ImportedProject.FullPath))
            .Concat(new[] { "Compile", "AdditionalFiles", "Analyzer", "AnalyzerConfigFiles", "EditorConfigFiles" }
                .SelectMany(itemType => project.GetItems(itemType).Select(item => item.GetMetadataValue("FullPath"))))
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(path => Path.GetFullPath(path))
            .Where(path => IsContained(root, path))
            .Select(path => resolver.RelativeIdentity(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return paths;
    }

    private static IReadOnlyList<string> AllowedOutputRoots(
        string root,
        MsBuildProject project,
        RepositoryPathResolver resolver)
    {
        var directory = Path.GetDirectoryName(project.FullPath)!;
        return new[] { "BaseIntermediateOutputPath", "IntermediateOutputPath", "BaseOutputPath", "OutputPath" }
            .Select(name => project.GetPropertyValue(name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(directory, value)))
            .Where(path => IsContained(root, path))
            .Select(path => resolver.RelativeIdentity(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
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

    private static IReadOnlyList<GeneratedSourceFact> RunGenerators(
        RoslynProject project,
        Compilation compilation,
        string projectIdentity,
        string contextRef,
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
        var facts = new List<GeneratedSourceFact>();
        foreach (var result in driver.GetRunResult().Results)
        {
            if (result.Exception is not null)
            {
                throw LoaderException.Generated("run.generated.authority-conflict");
            }

            var type = result.Generator.GetType();
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
            var producerId = "sgp-" + HashFramed("contract-scribe/sgp/v1", producerType, producerAssembly);
            foreach (var source in result.GeneratedSources)
            {
                var text = source.SourceText.ToString();
                facts.Add(new GeneratedSourceFact(
                    projectIdentity,
                    contextRef,
                    producerId,
                    "sgo-" + HashFramed("contract-scribe/sgo/v1", source.HintName),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                    text));
            }
        }

        return facts;
    }

    private static IReadOnlyList<GeneratedSourceFact> CreateToolGeneratedFacts(
        IReadOnlyList<ToolGeneratedSourceInput> inputs,
        IReadOnlyList<LoadedProject> projects)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var byIdentity = projects.ToDictionary(project => project.ProjectIdentity, StringComparer.Ordinal);
        var grammar = new System.Text.RegularExpressions.Regex(
            @"\A[A-Za-z][A-Za-z0-9._-]{0,127}\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var facts = new List<GeneratedSourceFact>(inputs.Count);
        foreach (var input in inputs)
        {
            if (!byIdentity.TryGetValue(input.ProjectIdentity, out var project)
                || !grammar.IsMatch(input.ProducerNamespace)
                || !grammar.IsMatch(input.ProducerName)
                || !grammar.IsMatch(input.OutputName))
            {
                throw LoaderException.Generated("run.generated.missing-identity");
            }

            var sourceBytes = Encoding.UTF8.GetBytes(input.SourceText);
            facts.Add(new GeneratedSourceFact(
                project.ProjectIdentity,
                project.CompilationContextRef,
                "tgp-" + HashFramed(
                    "contract-scribe/tgp/v1",
                    input.ProducerNamespace,
                    input.ProducerName),
                "tgo-" + HashFramed("contract-scribe/tgo/v1", input.OutputName),
                Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
                input.SourceText));
        }

        return facts;
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

    private static string ContextRef(string projectIdentity, string targetFramework) =>
        "ctx-" + HashFramed("contract-scribe/compilation-context/v1", projectIdentity, targetFramework);

    private static string HashFramed(string domain, params string[] fields)
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
            catch (EncoderFallbackException)
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

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static IReadOnlyList<LoaderDiagnostic> BoundDiagnostics(IEnumerable<LoaderDiagnostic> diagnostics) =>
        diagnostics
            .Distinct()
            .OrderBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Take(32)
            .ToArray();

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
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record EvaluatedProject(
    string Path,
    string Identity,
    string TargetFramework,
    bool IsRoot,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ProtectedPaths,
    IReadOnlyList<string> AllowedOutputRoots);

internal sealed record PostRegistrationResult(
    LoadedRepositorySession Session,
    IReadOnlyList<LoaderDiagnostic> Diagnostics,
    IReadOnlySet<string> ProtectedPaths,
    IReadOnlySet<string> AllowedOutputRoots);
