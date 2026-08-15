using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using ContractScribe.Agent.Runtime;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeArchitectureTests
{
    private static readonly string[] ForbiddenSourceTokens =
    [
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "Microsoft.CodeAnalysis",
        "ContractScribe.Roslyn",
        "ContractScribe.Patching",
        "ContractScribe.Cli",
        "ContractScribe.GitHub",
        "IServiceProvider",
        "HttpClient",
        "FileInfo",
        "DirectoryInfo",
        "TextWriter",
        "Environment.",
        "File.",
        "Directory.",
        "Process.",
        "Dictionary<",
        "Func<",
        "Action<",
    ];

    [Fact]
    public void Agent_project_has_one_core_reference_and_no_package_dependency()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Join(root, "src", "ContractScribe.Agent", "ContractScribe.Agent.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();

        Assert.Equal(new[] { "../ContractScribe.Core/ContractScribe.Core.csproj" }, references);
        Assert.Empty(project.Descendants("PackageReference"));

        var solution = File.ReadAllText(Path.Join(root, "ContractScribe.slnx"));
        Assert.Contains("src/ContractScribe.Agent/ContractScribe.Agent.csproj", solution, StringComparison.Ordinal);
        var tests = XDocument.Load(Path.Join(root, "tests", "ContractScribe.Tests", "ContractScribe.Tests.csproj"));
        Assert.Contains(tests.Descendants("ProjectReference"), element =>
            element.Attribute("Include")?.Value.Replace('\\', '/')
                == "../../src/ContractScribe.Agent/ContractScribe.Agent.csproj");

        var productReferences = typeof(DocumentationScribeRuntime).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("ContractScribe.", StringComparison.Ordinal) == true)
            .Select(name => name!)
            .ToArray();
        Assert.Equal(new[] { "ContractScribe.Core" }, productReferences);
    }

    [Fact]
    public void Public_agent_api_exposes_only_typed_closed_capabilities()
    {
        var assembly = typeof(DocumentationScribeRuntime).Assembly;
        var publicTypes = assembly.GetExportedTypes();
        var forbiddenTypes = new[]
        {
            typeof(object), typeof(Delegate), typeof(IServiceProvider), typeof(Stream),
            typeof(FileInfo), typeof(DirectoryInfo), typeof(TextWriter), typeof(Uri),
        };
        var forbiddenNames = new[]
        {
            "endpoint", "header", "credential", "environment", "workspace", "root",
            "path", "writer", "client", "serviceprovider", "capabilities",
        };

        foreach (var type in publicTypes)
        {
            Assert.False(typeof(IDictionary).IsAssignableFrom(type), type.FullName);
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    Assert.DoesNotContain(parameter.ParameterType, forbiddenTypes);
                    Assert.DoesNotContain(forbiddenNames, forbidden =>
                        parameter.Name?.Contains(forbidden, StringComparison.OrdinalIgnoreCase) == true);
                    Assert.False(IsDictionary(parameter.ParameterType), parameter.ParameterType.FullName);
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => method.DeclaringType == type))
            {
                Assert.DoesNotContain(method.ReturnType, forbiddenTypes);
                Assert.False(IsDictionary(method.ReturnType), method.ToString());
                Assert.All(method.GetParameters(), parameter =>
                {
                    Assert.DoesNotContain(parameter.ParameterType, forbiddenTypes);
                    Assert.False(IsDictionary(parameter.ParameterType), parameter.ParameterType.FullName);
                });
            }
        }
    }

    [Fact]
    public void Runtime_and_prompting_sources_have_no_mutation_discovery_or_general_network_capability()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Join(root, "src", "ContractScribe.Agent");
        var violations = new[] { "Runtime", "Prompting", "Properties" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Join(agentRoot, directory), "*.cs", SearchOption.AllDirectories))
            .SelectMany(path => FindForbiddenCapabilities(File.ReadAllText(path))
                .Select(token => $"{Path.GetRelativePath(root, path)}: {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Capability_scan_rejects_negative_examples()
    {
        const string hostile = """
            using System.IO;
            using System.Net.Http;
            public sealed class BadAgent
            {
                public BadAgent(IServiceProvider capabilities, Func<string> writer) { }
            }
            """;

        var violations = FindForbiddenCapabilities(hostile);
        Assert.Contains("System.IO", violations);
        Assert.Contains("System.Net", violations);
        Assert.Contains("IServiceProvider", violations);
        Assert.Contains("Func<", violations);
    }

    [Fact]
    public void Scripted_fake_is_internal_and_hermetic()
    {
        var assembly = typeof(DocumentationScribeRuntime).Assembly;
        var fake = Assert.Single(
            assembly.GetTypes(),
            type => type.Name == "ScriptedDocumentationScribeModelExchange");
        Assert.False(fake.IsPublic);
        Assert.Contains(typeof(IDocumentationScribeModelExchange), fake.GetInterfaces());

        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Join(
            root, "src", "ContractScribe.Agent", "Runtime", "ScriptedDocumentationScribeModelExchange.cs"));
        Assert.Empty(FindForbiddenCapabilities(source));
        Assert.DoesNotContain("secret", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cache", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_structure_records_the_implemented_agent_boundary()
    {
        var document = File.ReadAllText(Path.Join(
            FindRepositoryRoot(), "docs", "20_architecture", "project-structure.md"));
        Assert.Contains("### `ContractScribe.Agent`", document, StringComparison.Ordinal);
        Assert.Contains("Issue #103", document, StringComparison.Ordinal);
        Assert.Contains("Agent's only production dependency is `ContractScribe.Core`", document, StringComparison.Ordinal);
        Assert.DoesNotContain("### Candidate `ContractScribe.Agent`", document, StringComparison.Ordinal);
    }

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        || type.GetInterfaces().Any(candidate => candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));

    private static string[] FindForbiddenCapabilities(string source) => ForbiddenSourceTokens
        .Where(token => source.Contains(token, StringComparison.Ordinal))
        .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
