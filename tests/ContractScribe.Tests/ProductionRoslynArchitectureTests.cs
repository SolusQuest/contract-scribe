using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class ProductionRoslynArchitectureTests
{
    [Fact]
    public void ProductionRoslynProjectHasOnlyApprovedEdgesAndRuntimeExclusions()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "ContractScribe.Roslyn", "ContractScribe.Roslyn.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
        Assert.Equal(["../ContractScribe.Core/ContractScribe.Core.csproj"], references);

        var packages = project.Descendants("PackageReference")
            .ToDictionary(
                element => element.Attribute("Include")!.Value,
                element => element.Attribute("ExcludeAssets")?.Value,
                StringComparer.Ordinal);
        Assert.DoesNotContain(packages.Keys, package =>
            package.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
            || package.Contains("Agent", StringComparison.OrdinalIgnoreCase)
            || package.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("runtime", packages["Microsoft.Build"]);
        Assert.Equal("runtime", packages["Microsoft.Build.Framework"]);
        Assert.Equal("runtime", packages["Microsoft.Build.Tasks.Core"]);
        Assert.Equal("runtime", packages["Microsoft.Build.Utilities.Core"]);
        Assert.Equal("runtime", packages["Microsoft.NET.StringTools"]);
    }

    [Fact]
    public void CurrentProjectGraphUsesOnlyProductionRoslynAtRuntime()
    {
        var root = FindRepositoryRoot();
        var fastProject = XDocument.Load(Path.Combine(
            root,
            "tests",
            "ContractScribe.Tests",
            "ContractScribe.Tests.csproj"));
        var fastReferences = fastProject.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")!.Value)
            .ToArray();
        var integration = File.ReadAllText(Path.Combine(root, "tests", "ContractScribe.IntegrationTests", "ContractScribe.IntegrationTests.csproj"));
        Assert.DoesNotContain(fastReferences, reference =>
            reference.Contains("ContractScribe.Roslyn", StringComparison.Ordinal));
        Assert.Contains(@"../../src/ContractScribe.Roslyn/ContractScribe.Roslyn.csproj", integration, StringComparison.Ordinal);
        Assert.DoesNotContain(@"tests/ContractScribe.Roslyn", integration, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ContractScribe.ContractBaselineProbe.csproj",
            integration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClassificationConformanceOracle.cs",
            integration,
            StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.StringTools", integration, StringComparison.Ordinal);
        Assert.Equal(5, XDocument.Parse(integration).Descendants("PackageReference").Count(element =>
            element.Attribute("ExcludeAssets")?.Value == "runtime"));

        foreach (var sourceProjectPath in Directory.EnumerateFiles(
                     Path.Combine(root, "src"),
                     "*.csproj",
                     SearchOption.AllDirectories))
        {
            var sourceReferences = XDocument.Load(sourceProjectPath)
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value);
            Assert.DoesNotContain(sourceReferences, reference =>
                reference.Contains("tests", StringComparison.OrdinalIgnoreCase)
                || reference.Contains("Experiment", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CoreClassificationContractsRemainPlatformNeutral()
    {
        var root = FindRepositoryRoot();
        var coreProject = XDocument.Load(Path.Combine(
            root,
            "src",
            "ContractScribe.Core",
            "ContractScribe.Core.csproj"));
        Assert.Empty(coreProject.Descendants("PackageReference"));
        Assert.Empty(coreProject.Descendants("ProjectReference"));

        var contractTypes = typeof(ClassificationSet).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(ClassificationSet).Namespace)
            .Where(type =>
                type.Name.Contains("Classification", StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationObservation",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationDeclaration",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationSource",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationAuthority",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationBlock",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationComponentMatch",
                    StringComparison.Ordinal)
                || type.Name.Contains(
                    "DocumentationUnavailable",
                    StringComparison.Ordinal)
                || type == typeof(SymbolRef)
                || type == typeof(TargetProfile)
                || type == typeof(PrimarySymbolKind)
                || type == typeof(SymbolTrait)
                || type == typeof(ComponentKind)
                || type == typeof(RelationKind)
                || type == typeof(SupportStatus)
                || type == typeof(SkipReason)
                || type == typeof(CandidateLocator)
                || type == typeof(Utf16Span))
            .ToArray();
        var exposed = contractTypes
            .SelectMany(type => type.GetProperties()
                .Select(property => property.PropertyType)
                .Append(type))
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(exposed, type =>
            type.Assembly.GetName().Name is { } assemblyName
            && (assemblyName.StartsWith(
                    "Microsoft.CodeAnalysis",
                    StringComparison.Ordinal)
                || assemblyName.StartsWith(
                    "Microsoft.Build",
                    StringComparison.Ordinal)));

        var probe = XDocument.Load(Path.Combine(
            root,
            "tests",
            "ContractScribe.LoaderProbe",
            "ContractScribe.LoaderProbe.csproj"));
        Assert.Equal(
            ["../../src/ContractScribe.Roslyn/ContractScribe.Roslyn.csproj"],
            probe.Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value));
        Assert.DoesNotContain(
            probe.ToString(),
            "ContractScribe.Roslyn.Experiment",
            StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(
            root,
            "src",
            "ContractScribe.Core",
            "ClassificationNormalization.cs")));
        Assert.False(File.Exists(Path.Combine(
            root,
            "src",
            "ContractScribe.Roslyn",
            "ClassificationNormalization.cs")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "src",
            "ContractScribe.Core",
            "DocumentationObservationNormalization.cs")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "src",
            "ContractScribe.Roslyn",
            "DocumentationObserver.cs")));
    }

    [Fact]
    public void ClassificationResultsCannotBeForgedThroughThePublicCoreApi()
    {
        var resultTypes = new[]
        {
            typeof(SymbolRef),
            typeof(Utf16Span),
            typeof(RepositoryCandidateLocator),
            typeof(GeneratedSourceCandidateLocator),
            typeof(ToolGeneratedCandidateLocator),
            typeof(SyntheticCandidateLocator),
            typeof(TargetClassification),
            typeof(ComponentClassification),
            typeof(RelationObservation),
            typeof(UnresolvedClassification),
            typeof(ClassificationSet),
            typeof(RepositoryDocumentationSourceIdentity),
            typeof(GeneratedDocumentationSourceIdentity),
            typeof(DocumentationObservationSubject),
            typeof(DocumentationDeclarationFact),
            typeof(DocumentationObservation),
            typeof(DocumentationObservationSet),
        };
        Assert.All(resultTypes, type =>
            Assert.Empty(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public)));

        var locatorConstructors = typeof(CandidateLocator).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotEmpty(locatorConstructors);
        Assert.All(locatorConstructors, constructor =>
            Assert.True(
                constructor.IsFamilyAndAssembly,
                "CandidateLocator must remain closed to external subclasses."));

        Assert.Null(typeof(ClassificationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
        Assert.NotNull(typeof(ClassificationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(DocumentationObservationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
        Assert.NotNull(typeof(DocumentationObservationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.NonPublic));

        var sourceConstructors =
            typeof(DocumentationSourceIdentity).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotEmpty(sourceConstructors);
        Assert.All(sourceConstructors, constructor =>
            Assert.True(
                constructor.IsFamilyAndAssembly,
                "DocumentationSourceIdentity must remain closed to external subclasses."));
    }

    [Fact]
    public void CoreInternalsHaveNoProductionAssemblyFriends()
    {
        var friends = typeof(ClassificationSet).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute =>
                attribute.AssemblyName.Split(',', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ContractScribe.IntegrationTests"], friends);
    }

    [Fact]
    public void ProductionDocumentationObservationHasNoExpandedOrExternalRetrieval()
    {
        var root = FindRepositoryRoot();
        var observer = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ContractScribe.Roslyn",
            "DocumentationObserver.cs"));

        Assert.DoesNotContain(
            "GetDocumentationCommentXml",
            observer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("System.Xml", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Read", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Open", observer, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAssembliesCannotDeriveCandidateLocators()
    {
        const string source = """
            using ContractScribe.Core;

            public sealed class ForeignLocator : CandidateLocator
            {
                public override int GetHashCode() => 0;

                protected override bool EqualsCore(CandidateLocator other) => true;
            }
            """;
        var platformReferences = ((string)AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "ForeignCandidateLocator",
            [CSharpSyntaxTree.ParseText(source)],
            platformReferences.Append(MetadataReference.CreateFromFile(
                typeof(CandidateLocator).Assembly.Location)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Contains(errors, diagnostic =>
            diagnostic.Id is "CS1729" or "CS0122");
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.IsArray && type.GetElementType() is { } element)
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
