using System.Reflection;
using System.Xml.Linq;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Patching.Resolution;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class DocumentationPatchArchitectureTests
{
    [Fact]
    public void PatchingProjectHasOnlyTheReadOnlyCoreAndRoslynEdges()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Join(
            root,
            "src",
            "ContractScribe.Patching",
            "ContractScribe.Patching.csproj"));

        Assert.Equal(
            [
                "../ContractScribe.Core/ContractScribe.Core.csproj",
                "../ContractScribe.Roslyn/ContractScribe.Roslyn.csproj",
            ],
            project.Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value));
        Assert.Empty(project.Descendants("PackageReference"));

        foreach (var projectPath in Directory.EnumerateFiles(
            Path.Join(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories).Where(path =>
                !path.Contains("ContractScribe.Patching", StringComparison.Ordinal)
                && !path.Contains("ContractScribe.Cli", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                XDocument.Load(projectPath).Descendants("ProjectReference"),
                reference => reference.Attribute("Include")!.Value.Contains(
                    "ContractScribe.Patching",
                    StringComparison.Ordinal));
        }

        var cli = XDocument.Load(Path.Join(
            root,
            "src",
            "ContractScribe.Cli",
            "ContractScribe.Cli.csproj"));
        Assert.Contains(
            cli.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")!.Value.Replace('\\', '/')
                == "../ContractScribe.Patching/ContractScribe.Patching.csproj");

        AssertDirectReference(root, "ContractScribe.Tests");
        AssertDirectReference(root, "ContractScribe.IntegrationTests");
    }

    [Fact]
    public void ResolutionHandoffIsImmutableAndPlatformNeutral()
    {
        var exposed = typeof(DocumentationPatchResolutionResult).Assembly
            .GetExportedTypes()
            .SelectMany(PublicSignatureTypes)
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(exposed, type =>
            type.Assembly.GetName().Name is { } name
            && (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Build", StringComparison.Ordinal)));
        Assert.DoesNotContain(exposed, type =>
            type.Name.Contains("Workspace", StringComparison.Ordinal)
            || type.Name.Contains("Syntax", StringComparison.Ordinal)
            || type.Name.Contains("Writer", StringComparison.Ordinal)
            || type == typeof(FileInfo)
            || type == typeof(DirectoryInfo));

        Assert.All(
            new[]
            {
                typeof(ResolvedDocumentationPatchTarget),
                typeof(DocumentationPatchApplicableComponentFact),
                typeof(DocumentationPatchResolutionResult),
            },
            type => Assert.Empty(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public)));
    }

    [Fact]
    public void CliFriendCannotInjectRepositoryContextEntropy()
    {
        const string source = """
            using System;
            using ContractScribe.Roslyn;

            public static class Probe
            {
                public static RepositoryLoader Create(Func<int, byte[]> bytes) =>
                    new RepositoryLoader(null, null, null, null, null, null, bytes);
            }
            """;
        var platformReferences = ((string)AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "ContractScribe.Cli",
            [CSharpSyntaxTree.ParseText(source)],
            platformReferences
                .Append(MetadataReference.CreateFromFile(
                    typeof(DocumentationPatchRequest).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(
                    typeof(ContractScribe.Roslyn.RepositoryLoader).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Contains(errors, diagnostic => diagnostic.Id is "CS1729" or "CS0122");
    }

    [Fact]
    public void LoadedSessionUsesOnlyTypedNonPersistentRepositoryContext()
    {
        Assert.Null(typeof(LoadedRepositorySession).GetProperty("RepositoryIdentity"));
        Assert.Equal(
            typeof(RepositoryContextRef),
            typeof(LoadedRepositorySession).GetProperty(
                "RepositoryContextRef")!.PropertyType);
        Assert.Empty(Assert.Single(typeof(RepositoryLoader).GetConstructors())
            .GetParameters());

        var root = FindRepositoryRoot();
        var loader = File.ReadAllText(Path.Join(
            root,
            "src",
            "ContractScribe.Roslyn",
            "RepositoryLoader.cs"));
        Assert.Contains("RandomNumberGenerator.GetBytes", loader, StringComparison.Ordinal);
        Assert.Contains("repositoryContextBytes(16)", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.NewGuid", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("RepositoryContextRegistry", loader, StringComparison.Ordinal);
    }

    [Fact]
    public void RoslynDeclarationBatchExposesNoWorkspaceOrAbsolutePathCapability()
    {
        var exposed = typeof(DocumentationPatchDeclarationBatch).Assembly
            .GetExportedTypes()
            .Where(type => type.Name.StartsWith(
                "DocumentationPatchResolved",
                StringComparison.Ordinal)
                || type.Name.StartsWith(
                    "DocumentationPatchDeclaration",
                    StringComparison.Ordinal))
            .SelectMany(PublicSignatureTypes)
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(exposed, type =>
            type.Assembly.GetName().Name is { } name
            && (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Build", StringComparison.Ordinal)));
        Assert.DoesNotContain(exposed, type =>
            type.Name.Contains("Workspace", StringComparison.Ordinal)
            || type.Name.Contains("Syntax", StringComparison.Ordinal)
            || type.Name.Contains("Absolute", StringComparison.Ordinal));
        Assert.All(
            new[]
            {
                typeof(DocumentationPatchDeclarationFailure),
                typeof(DocumentationPatchResolvedComponent),
                typeof(DocumentationPatchResolvedDeclaration),
                typeof(DocumentationPatchDeclarationBlock),
                typeof(DocumentationPatchDeclarationBatch),
            },
            type => Assert.Empty(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public)));
    }

    [Fact]
    public void PatchingOwnsOnlyTheOpaqueCandidateWriteSurface()
    {
        var root = FindRepositoryRoot();
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                Path.Join(root, "src", "ContractScribe.Patching"),
                "*.cs",
                SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.Contains("DocumentationPatchRenderer", source, StringComparison.Ordinal);
        Assert.Contains("CreateNewRegularFile", source, StringComparison.Ordinal);
        Assert.Contains("DocumentationPatchEngine", source, StringComparison.Ordinal);
        Assert.Contains("DocumentationPatchValidationResult", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "File.Write",
            "File.Create",
            "StreamWriter",
            "MSBuildWorkspace",
            "HttpClient",
            "Octokit",
            "GitHub",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        var publicTypes = typeof(CandidatePatchApplicator).Assembly.GetExportedTypes();
        var exposed = publicTypes
            .SelectMany(PublicSignatureTypes)
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(exposed, type =>
            type.Assembly.GetName().Name is { } name
            && (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Build", StringComparison.Ordinal)));
        Assert.DoesNotContain(exposed, type =>
            type.Name.Contains("Workspace", StringComparison.Ordinal)
            || type.Name.Contains("Syntax", StringComparison.Ordinal)
            || type.Name.Contains("Writer", StringComparison.Ordinal)
            || typeof(Stream).IsAssignableFrom(type)
            || type == typeof(FileInfo)
            || type == typeof(DirectoryInfo));
        Assert.DoesNotContain(
            publicTypes.SelectMany(type => type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public)),
            property => property.Name.Contains("RootPath", StringComparison.Ordinal)
                || property.Name.Contains("WorkspacePath", StringComparison.Ordinal)
                || property.Name.Contains("PhysicalPath", StringComparison.Ordinal));

        Assert.Equal(
            [nameof(DocumentationPatchCandidateHandle.IsInvalidated)],
            typeof(DocumentationPatchCandidateHandle)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name));
        Assert.Empty(typeof(DocumentationPatchCandidateHandle).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(DocumentationPatchApplicationResult).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(DocumentationPatchExecutionOutcome).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(DocumentationPatchAcceptedCandidate).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(DocumentationPatchAcceptedCandidate).GetProperties(
                BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains("Path", StringComparison.Ordinal)
                || property.Name.Contains("Root", StringComparison.Ordinal)
                || property.Name.Contains("Writer", StringComparison.Ordinal));

        var roslynFriends = File.ReadAllText(Path.Join(
            root,
            "src",
            "ContractScribe.Roslyn",
            "Properties",
            "AssemblyInfo.cs"));
        Assert.DoesNotContain(
            "InternalsVisibleTo(\"ContractScribe.Patching\")",
            roslynFriends,
            StringComparison.Ordinal);
    }

    private static void AssertDirectReference(string root, string projectName)
    {
        var project = XDocument.Load(Path.Join(
            root,
            "tests",
            projectName,
            projectName + ".csproj"));
        Assert.Contains(
            project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")!.Value.Contains(
                "ContractScribe.Patching",
                StringComparison.Ordinal));
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in ExpandType(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in ExpandType(argument))
            {
                yield return nested;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
