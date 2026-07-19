using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace ContractScribe.Roslyn;

public sealed class FrameworkDependentExperiment
{
    private static readonly StringComparer Ordinal = StringComparer.Ordinal;

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
        try
        {
            RegisterMsbuild();

            using var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (_, eventArgs) =>
            {
                var isFailure = eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure;
                workspaceDiagnostics.Add(new DiagnosticRecord(
                    ClassifyWorkspaceDiagnostic(eventArgs.Diagnostic.Message),
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
                .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                .OrderBy(project => project.Name, Ordinal)
                .ToArray();

            if (!projects.Select(project => project.Name).SequenceEqual(
                    new[] { "SampleApp", "SampleLibrary" },
                    Ordinal))
            {
                throw new ExperimentFailureException(
                    FailurePhase.WorkspaceLoad,
                    "workspace.project-graph-mismatch");
            }

            var sampleApp = projects.Single(project => project.Name == "SampleApp");
            var projectReferenceNames = sampleApp.ProjectReferences
                .Select(reference => solution.GetProject(reference.ProjectId)?.Name)
                .Where(name => name is not null)
                .Cast<string>()
                .OrderBy(name => name, Ordinal)
                .ToArray();

            if (!projectReferenceNames.SequenceEqual(new[] { "SampleLibrary" }, Ordinal))
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
                payloadBytes = SemanticPayloadSerializer.Serialize(payload);
            }
            catch (Exception)
            {
                return Failure(
                    ExperimentStatus.ClassifiedFailure,
                    FailurePhase.Serialization,
                    "serialization.semantic-payload-failed",
                    workspaceDiagnostics);
            }

            if (workspaceDiagnostics.Any(diagnostic => diagnostic.Severity == "failure"))
            {
                var firstFailure = workspaceDiagnostics.First(diagnostic => diagnostic.Severity == "failure");
                var phase = firstFailure.Code == "msbuild.sdk-unavailable"
                    ? FailurePhase.MsbuildEnvironment
                    : FailurePhase.WorkspaceLoad;
                return Failure(
                    ExperimentStatus.ClassifiedFailure,
                    phase,
                    firstFailure.Code,
                    workspaceDiagnostics);
            }

            return new ExperimentExecution(
                ExperimentResult.Success(payload) with { Diagnostics = workspaceDiagnostics },
                payloadBytes);
        }
        catch (OperationCanceledException)
        {
            return Failure(ExperimentStatus.InternalError, null, null, workspaceDiagnostics);
        }
        catch (ExperimentFailureException exception)
        {
            var status = exception.Phase == FailurePhase.Input
                ? ExperimentStatus.InvalidInput
                : ExperimentStatus.ClassifiedFailure;
            return Failure(status, exception.Phase, exception.Code, workspaceDiagnostics);
        }
        catch (Exception)
        {
            return Failure(ExperimentStatus.InternalError, null, null, workspaceDiagnostics);
        }
    }

    private static void RegisterMsbuild()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception)
        {
            throw new ExperimentFailureException(
                FailurePhase.MsbuildEnvironment,
                "msbuild.registration-failed");
        }
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

    private static string ClassifyWorkspaceDiagnostic(string message)
    {
        return message.Contains("SDK", StringComparison.OrdinalIgnoreCase)
            || message.Contains("targeting pack", StringComparison.OrdinalIgnoreCase)
            ? "msbuild.sdk-unavailable"
            : "workspace.solution-load-failed";
    }

    private static ExperimentExecution Failure(
        ExperimentStatus status,
        FailurePhase? phase,
        string? code,
        IReadOnlyList<DiagnosticRecord>? diagnostics = null)
    {
        return new ExperimentExecution(
            ExperimentResult.Failure(status, phase, code, diagnostics),
            null);
    }
}
