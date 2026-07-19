using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContractScribe.Roslyn;

public sealed class FrameworkDependentExperiment
{
    private static readonly StringComparer Ordinal = StringComparer.Ordinal;
    private static ToolchainIdentity? registeredToolchain;
    private readonly Func<SemanticPayload, byte[]> serializePayload;

    public FrameworkDependentExperiment(Func<SemanticPayload, byte[]>? payloadSerializer = null)
    {
        serializePayload = payloadSerializer ?? SemanticPayloadSerializer.Serialize;
    }

    public async Task<ExperimentExecution> RunAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return Failure(ExperimentStatus.InvalidInput, FailurePhase.Input, "input.solution-not-found");
        }

        if (!Path.IsPathFullyQualified(solutionPath) || !solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ExperimentStatus.InvalidInput, FailurePhase.Input, "input.solution-not-supported");
        }

        if (!File.Exists(solutionPath))
        {
            return Failure(ExperimentStatus.InvalidInput, FailurePhase.Input, "input.solution-not-found");
        }

        var workspaceDiagnostics = new List<DiagnosticRecord>();
        ToolchainIdentity? toolchain = null;
        try
        {
            toolchain = RegisterMsbuild(solutionPath);

            using var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (_, eventArgs) =>
            {
                var isFailure = eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure;
                workspaceDiagnostics.Add(new DiagnosticRecord(
                    FailureClassifier.ClassifyWorkspaceDiagnostic(eventArgs.Diagnostic.Message),
                    isFailure ? "failure" : "warning"));
            };

            Solution solution;
            try
            {
                solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new ExperimentFailureException(
                    FailurePhase.WorkspaceLoad,
                    "workspace.solution-load-failed");
            }

            var projects = solution.Projects
                .OrderBy(project => project.Name, Ordinal)
                .ToArray();

            if (workspaceDiagnostics.Any(diagnostic => diagnostic.Severity == "failure"))
            {
                var firstFailure = SelectWorkspaceFailure(workspaceDiagnostics);
                return Failure(
                    ExperimentStatus.ClassifiedFailure,
                    firstFailure.Phase,
                    firstFailure.Code,
                    workspaceDiagnostics,
                    toolchain);
            }

            if (projects.Length != 2
                || !projects.Select(project => project.Name).SequenceEqual(
                    new[] { "SampleApp", "SampleLibrary" },
                    Ordinal)
                || projects.Any(project => !string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal)))
            {
                throw new ExperimentFailureException(
                    FailurePhase.WorkspaceLoad,
                    "workspace.project-graph-mismatch");
            }

            var edges = projects
                .SelectMany(project => project.ProjectReferences.Select(reference =>
                    (From: project.Name, To: solution.GetProject(reference.ProjectId)?.Name)))
                .OrderBy(edge => edge.From, Ordinal)
                .ThenBy(edge => edge.To ?? string.Empty, Ordinal)
                .ToArray();

            if (edges.Length != 1
                || edges[0].To is null
                || !string.Equals(edges[0].From, "SampleApp", StringComparison.Ordinal)
                || !string.Equals(edges[0].To, "SampleLibrary", StringComparison.Ordinal))
            {
                throw new ExperimentFailureException(
                    FailurePhase.WorkspaceLoad,
                    "workspace.project-graph-mismatch");
            }

            var payloads = new List<ProjectPayload>(projects.Length);
            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is null)
                {
                    throw new ExperimentFailureException(
                        FailurePhase.Compilation,
                        "compilation.errors");
                }

                if (compilation.GetDiagnostics(cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    throw new ExperimentFailureException(
                        FailurePhase.Compilation,
                        "compilation.errors");
                }

                if (project.Name == "SampleApp")
                {
                    var referencedAssemblyNames = compilation.References
                        .Select(reference => compilation.GetAssemblyOrModuleSymbol(reference))
                        .OfType<IAssemblySymbol>()
                        .Select(assembly => assembly.Name)
                        .ToHashSet(Ordinal);
                    if (!referencedAssemblyNames.Contains("SampleLibrary"))
                    {
                        throw new ExperimentFailureException(
                            FailurePhase.Compilation,
                            "compilation.reference-missing");
                    }
                }

                var sourceSymbols = EnumeratePublicSourceSymbols(compilation.Assembly.GlobalNamespace);
                payloads.Add(new ProjectPayload(
                    project.Name,
                    sourceSymbols
                        .OrderBy(symbol => symbol.DocumentationCommentId, Ordinal)
                        .ToArray()));
            }

            var payload = new SemanticPayload(payloads.OrderBy(project => project.ProjectId, Ordinal).ToArray());
            byte[] payloadBytes;
            try
            {
                payloadBytes = serializePayload(payload);
            }
            catch (Exception)
            {
                return Failure(
                    ExperimentStatus.ClassifiedFailure,
                    FailurePhase.Serialization,
                    "serialization.semantic-payload-failed",
                    workspaceDiagnostics,
                    toolchain);
            }

            return new ExperimentExecution(
                ExperimentResult.Success(payload) with { Diagnostics = workspaceDiagnostics, Toolchain = toolchain },
                payloadBytes);
        }
        catch (OperationCanceledException)
        {
            return Failure(ExperimentStatus.InternalError, null, null, workspaceDiagnostics, toolchain);
        }
        catch (ExperimentFailureException exception)
        {
            var status = exception.Phase == FailurePhase.Input
                ? ExperimentStatus.InvalidInput
                : ExperimentStatus.ClassifiedFailure;
            return Failure(status, exception.Phase, exception.Code, workspaceDiagnostics, toolchain);
        }
        catch (Exception)
        {
            return Failure(ExperimentStatus.InternalError, null, null, workspaceDiagnostics, toolchain);
        }
    }

    private static ToolchainIdentity RegisterMsbuild(string solutionPath)
    {
        if (MSBuildLocator.IsRegistered)
        {
            return registeredToolchain
                ?? throw new ExperimentFailureException(
                    FailurePhase.MsbuildEnvironment,
                    "msbuild.registration-failed");
        }

        try
        {
            var expectedSdkVersion = ReadExpectedSdkVersion(solutionPath);
            MSBuildLocator.AllowQueryAllDotnetLocations = true;
            var selected = MSBuildLocator.QueryVisualStudioInstances()
                .Where(instance => instance.DiscoveryType == DiscoveryType.DotNetSdk)
                .Where(instance => string.Equals(GetSdkVersion(instance), expectedSdkVersion, StringComparison.Ordinal))
                .OrderByDescending(instance => instance.Version)
                .FirstOrDefault();
            if (selected is null)
            {
                throw new ExperimentFailureException(
                    FailurePhase.MsbuildEnvironment,
                    "msbuild.sdk-unavailable");
            }

            MSBuildLocator.RegisterInstance(selected);
            registeredToolchain = new ToolchainIdentity(
                expectedSdkVersion,
                FileVersionInfo.GetVersionInfo(Path.Combine(selected.MSBuildPath, "Microsoft.Build.dll")).FileVersion
                    ?? "unknown",
                selected.DiscoveryType.ToString(),
                Environment.Version.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString());
            return registeredToolchain;
        }
        catch (ExperimentFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ExperimentFailureException(
                FailurePhase.MsbuildEnvironment,
                "msbuild.registration-failed");
        }
    }

    private static string ReadExpectedSdkVersion(string solutionPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(solutionPath)!);
        while (directory is not null)
        {
            var globalJson = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(globalJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(globalJson));
                return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
                    ?? throw new ExperimentFailureException(
                        FailurePhase.MsbuildEnvironment,
                        "msbuild.sdk-unavailable");
            }

            directory = directory.Parent;
        }

        throw new ExperimentFailureException(
            FailurePhase.MsbuildEnvironment,
            "msbuild.sdk-unavailable");
    }

    private static string? GetSdkVersion(VisualStudioInstance instance)
    {
        foreach (var path in new[] { instance.VisualStudioRootPath, instance.MSBuildPath }.Where(path => path is not null))
        {
            var match = Regex.Match(path!, @"(?:^|[\\/])(?<version>\d+\.\d+\.\d+)(?:[\\/]|$)");
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }

    private static (FailurePhase Phase, string Code) SelectWorkspaceFailure(
        IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        if (diagnostics.Any(diagnostic => diagnostic.Code == "msbuild.sdk-unavailable"))
        {
            return (FailurePhase.MsbuildEnvironment, "msbuild.sdk-unavailable");
        }

        return (FailurePhase.WorkspaceLoad, "workspace.solution-load-failed");
    }

    private static IEnumerable<SymbolRecord> EnumeratePublicSourceSymbols(INamespaceSymbol root)
    {
        var seen = new HashSet<string>(Ordinal);
        foreach (var symbol in EnumerateNamespaceMembers(root))
        {
            if (symbol.IsImplicitlyDeclared || !IsSourceDeclared(symbol))
            {
                continue;
            }

            if (symbol.DeclaredAccessibility != Accessibility.Public || !HasPublicContainingTypes(symbol))
            {
                continue;
            }

            var documentationCommentId = symbol.GetDocumentationCommentId();
            if (string.IsNullOrWhiteSpace(documentationCommentId))
            {
                throw new ExperimentFailureException(
                    FailurePhase.SymbolIdentity,
                    "symbol.missing-documentation-id");
            }

            if (!seen.Add(documentationCommentId))
            {
                throw new ExperimentFailureException(
                    FailurePhase.SymbolIdentity,
                    "symbol.duplicate-identity");
            }

            yield return new SymbolRecord(documentationCommentId, symbol.Kind.ToString(), symbol.Name);
        }
    }

    private static IEnumerable<ISymbol> EnumerateNamespaceMembers(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetMembers().OrderBy(member => member.Name, Ordinal))
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (var childMember in EnumerateNamespaceMembers(childNamespace))
                {
                    yield return childMember;
                }

                continue;
            }

            if (member is INamedTypeSymbol namedType)
            {
                foreach (var symbol in EnumerateTypeMembers(namedType))
                {
                    yield return symbol;
                }
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateTypeMembers(INamedTypeSymbol namedType)
    {
        yield return namedType;

        foreach (var member in namedType.GetMembers().OrderBy(member => member.Name, Ordinal))
        {
            if (member is INamedTypeSymbol nestedType)
            {
                foreach (var symbol in EnumerateTypeMembers(nestedType))
                {
                    yield return symbol;
                }

                continue;
            }

            yield return member;
        }
    }

    private static bool IsSourceDeclared(ISymbol symbol)
    {
        return symbol.Locations.Any(location => location.IsInSource)
            && symbol.DeclaringSyntaxReferences.Length > 0;
    }

    private static bool HasPublicContainingTypes(ISymbol symbol)
    {
        for (var containingType = symbol.ContainingType;
             containingType is not null;
             containingType = containingType.ContainingType)
        {
            if (containingType.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static ExperimentExecution Failure(
        ExperimentStatus status,
        FailurePhase? phase,
        string? code,
        IReadOnlyList<DiagnosticRecord>? diagnostics = null,
        ToolchainIdentity? toolchain = null)
    {
        return new ExperimentExecution(
            ExperimentResult.Failure(status, phase, code, diagnostics) with { Toolchain = toolchain },
            null);
    }
}
