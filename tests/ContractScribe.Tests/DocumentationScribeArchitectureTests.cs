using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeArchitectureTests
{
    [Fact]
    public void Campaign_completion_registrar_is_internal_to_the_explicit_X1_friend_boundary()
    {
        var core = typeof(CampaignProviderCompletionRegistrar).Assembly;
        var friends = core.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .ToArray();
        Assert.Contains("ContractScribe.Cli", friends);
        Assert.Equal(
            ["ContractScribe.Cli"],
            friends.Where(name => name.StartsWith("ContractScribe.", StringComparison.Ordinal)
                    && !name.Contains("Tests", StringComparison.Ordinal))
                .ToArray());
        Assert.False(typeof(CampaignProviderCompletionRegistrar).IsPublic);
        Assert.False(typeof(CampaignProviderCompletionKind).IsPublic);
        Assert.True(typeof(CampaignProviderInvocationAuthority)
            .GetMethod("TryCreateCompletionRegistrar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .IsAssembly);
        Assert.True(typeof(CampaignProviderCompletionRegistrar)
            .GetMethod("TryAuthorizePreparation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .IsAssembly);
        Assert.True(typeof(CampaignProviderCompletionRegistrar)
            .GetMethod("TryRegister", BindingFlags.Instance | BindingFlags.NonPublic)!
            .IsAssembly);

        var root = FindRepositoryRoot();
        var productionUses = Directory.EnumerateFiles(Path.Join(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Where(item => item.Source.Contains("TryCreateCompletionRegistrar", StringComparison.Ordinal)
                || item.Source.Contains("TryAuthorizePreparation", StringComparison.Ordinal)
                || item.Source.Contains("TryRegister(", StringComparison.Ordinal))
            .ToArray();
        Assert.All(productionUses, item => Assert.True(
            item.Path.EndsWith("DocumentationScribeComposition.cs", StringComparison.OrdinalIgnoreCase)
                || item.Path.EndsWith("CampaignProviderCompletion.cs", StringComparison.OrdinalIgnoreCase)
                || item.Path.EndsWith("CampaignStateReducer.cs", StringComparison.OrdinalIgnoreCase),
            item.Path));
        Assert.Single(productionUses, item =>
            item.Path.EndsWith("DocumentationScribeComposition.cs", StringComparison.OrdinalIgnoreCase));

        var completion = Assert.Single(typeof(CampaignStateReducer).GetMethods(),
            method => method.Name == nameof(CampaignStateReducer.CompleteProviderInvocation));
        Assert.Contains(completion.GetParameters(), parameter =>
            parameter.ParameterType == typeof(CampaignProviderCompletionAuthority));
        Assert.DoesNotContain(completion.GetParameters(), parameter =>
            parameter.ParameterType == typeof(DocumentationScribeValidatedRunOutcome));
    }

    [Fact]
    public void Agent_project_keeps_the_core_only_project_edge_and_confines_provider_packages()
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
        var providerRoot = Path.Join(root, "src", "ContractScribe.Agent", "Providers");
        if (!Directory.Exists(providerRoot)
            || !Directory.EnumerateFiles(providerRoot, "*.cs", SearchOption.AllDirectories).Any())
        {
            Assert.Empty(project.Descendants("PackageReference"));
        }

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

        foreach (var productProject in Directory.EnumerateFiles(
            Path.Join(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories).Where(path => !string.Equals(
                path,
                projectPath,
                StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    "ContractScribe.Cli",
                    StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                XDocument.Load(productProject).Descendants("ProjectReference"),
                reference => reference.Attribute("Include")?.Value.Contains(
                    "ContractScribe.Agent",
                    StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Cli_is_the_only_production_composer_of_agent_and_patching()
    {
        var root = FindRepositoryRoot();
        var cliPath = Path.Join(root, "src", "ContractScribe.Cli", "ContractScribe.Cli.csproj");
        var cli = XDocument.Load(cliPath);
        Assert.Equal(
            new[]
            {
                "../ContractScribe.Agent/ContractScribe.Agent.csproj",
                "../ContractScribe.Core/ContractScribe.Core.csproj",
                "../ContractScribe.Patching/ContractScribe.Patching.csproj",
                "../ContractScribe.Roslyn/ContractScribe.Roslyn.csproj",
            },
            cli.Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());

        foreach (var projectPath in Directory.EnumerateFiles(
            Path.Join(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories).Where(path => !string.Equals(
                path,
                cliPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            var references = XDocument.Load(projectPath).Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value);
            Assert.DoesNotContain(references, reference =>
                reference.Contains("ContractScribe.Agent", StringComparison.Ordinal));
        }

        var composition = File.ReadAllText(Path.Join(
            root,
            "src",
            "ContractScribe.Cli",
            "DocumentationScribeComposition.cs"));
        Assert.DoesNotContain("File.Write", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Create", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_agent_api_exposes_only_typed_closed_capabilities()
    {
        var assembly = typeof(DocumentationScribeRuntime).Assembly;
        var publicTypes = ReachableAgentApiTypes(assembly.GetExportedTypes()
            .Where(type => type.Namespace is "ContractScribe.Agent.Runtime"
                or "ContractScribe.Agent.Prompting")
            .ToArray(), assembly).ToArray();
        var forbiddenNames = new[]
        {
            "endpoint", "header", "credential", "environment", "workspace", "root",
            "path", "writer", "client", "serviceprovider", "capabilities",
        };

        foreach (var type in publicTypes)
        {
            var signatureTypes = PublicSignatureTypes(type).SelectMany(ExpandType).Distinct().ToArray();
            Assert.DoesNotContain(signatureTypes, IsForbiddenPublicType);
            Assert.All(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()), parameter =>
                    Assert.DoesNotContain(forbiddenNames, forbidden =>
                        parameter.Name?.Contains(forbidden, StringComparison.OrdinalIgnoreCase) == true));
        }
    }

    [Fact]
    public void Provider_diagnostics_keep_the_existing_friend_boundary_and_expose_no_raw_capability()
    {
        var assembly = typeof(OpenAiCompatibleResponseDiagnostics).Assembly;
        Assert.Equal(
            ["ContractScribe.Tests"],
            assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(attribute => attribute.AssemblyName)
                .Order(StringComparer.Ordinal)
                .ToArray());

        Type[] diagnosticTypes =
        [
            typeof(OpenAiCompatibleResponseDiagnostics),
            typeof(OpenAiCompatibleResponseDiagnostic),
            typeof(OpenAiCompatibleResponseDiagnosticCase),
            typeof(OpenAiCompatibleResponseCodecDisposition),
        ];
        Assert.All(diagnosticTypes.Where(type => type.IsClass), type => Assert.True(type.IsSealed));
        Assert.All(diagnosticTypes, type => Assert.True(type.IsPublic));

        var publicMembers = diagnosticTypes.Where(type => !type.IsEnum).SelectMany(type =>
            type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(member => member.DeclaringType == type))
            .ToArray();
        var forbiddenNames = new[]
        {
            "requestBytes", "responseBody", "rawResponse", "header", "credential",
            "preparedRequest", "continuation", "callback", "stream",
        };
        Assert.DoesNotContain(publicMembers, member => forbiddenNames.Any(forbidden =>
            member.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));

        var signatureTypes = diagnosticTypes
            .SelectMany(PublicSignatureTypes)
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(typeof(byte[]), signatureTypes);
        Assert.DoesNotContain(typeof(Stream), signatureTypes);
        Assert.DoesNotContain(signatureTypes, type =>
            type.FullName?.StartsWith("System.Net.Http.Http", StringComparison.Ordinal) == true
            || typeof(Delegate).IsAssignableFrom(type));

        var structure = File.ReadAllText(Path.Join(
            FindRepositoryRoot(), "docs", "20_architecture", "project-structure.md"));
        Assert.Contains("Issue #110 adds a narrow provider-diagnostic capability", structure, StringComparison.Ordinal);
        Assert.Contains("Evaluation receives no Agent friendship", structure, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_and_prompting_sources_have_no_mutation_discovery_or_general_network_capability()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Join(root, "src", "ContractScribe.Agent");
        var sources = new[] { "Runtime", "Prompting", "Properties" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Join(agentRoot, directory), "*.cs", SearchOption.AllDirectories))
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllText, StringComparer.Ordinal);
        var violations = FindForbiddenCapabilities(sources);

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("class Bad { Stream Open(string path) => new FileStream(path, FileMode.Open); }")]
    [InlineData("using Disk = System.IO.File; class Bad { string Read() => Disk.ReadAllText(\"x\"); }")]
    [InlineData("class Bad { string Read() => Helper(); string Helper() => File.ReadAllText(\"x\"); }")]
    [InlineData("public delegate string RuntimeFactory();")]
    [InlineData("class Bad { List<List<Stream>> Values { get; } = []; }")]
    [InlineData("class Bad { HttpClient Client { get; } = new(); }")]
    [InlineData("using E = System.Environment; class Bad { string? Read() => E.GetEnvironmentVariable(\"CONTRACTSCRIBE_SECRET\"); }")]
    [InlineData("class Bad { DateTime Observe() => DateTime.UtcNow; }")]
    [InlineData("class Bad { DateTimeOffset Observe() => DateTimeOffset.Now; }")]
    [InlineData("class Bad { TimeZoneInfo Observe() => TimeZoneInfo.Local; }")]
    [InlineData("using System.Globalization; class Bad { string Observe() => Helper(); string Helper() => CultureInfo.CurrentCulture.Name; }")]
    public void Capability_scan_rejects_semantic_negative_examples(string hostile)
    {
        var violations = FindForbiddenCapabilities(new Dictionary<string, string>
        {
            ["Hostile.cs"] = hostile,
        });

        Assert.NotEmpty(violations);
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
        Assert.Empty(FindForbiddenCapabilities(new Dictionary<string, string>
        {
            ["ScriptedDocumentationScribeModelExchange.cs"] = source,
        }));
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
        Assert.Contains("Agent's only production project dependency is `ContractScribe.Core`", document, StringComparison.Ordinal);
        Assert.DoesNotContain("### Candidate `ContractScribe.Agent`", document, StringComparison.Ordinal);
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        yield return type;
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.DeclaringType == type
                && method.Name is not (nameof(ToString) or nameof(Equals) or nameof(GetHashCode))))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            yield return property.PropertyType;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            yield return field.FieldType;
        }

        foreach (var eventType in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(eventInfo => eventInfo.EventHandlerType)
            .Where(eventType => eventType is not null))
        {
            yield return eventType!;
        }
    }

    private static IEnumerable<Type> ReachableAgentApiTypes(
        IEnumerable<Type> roots,
        Assembly agentAssembly)
    {
        var pending = new Queue<Type>(roots);
        var seen = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            yield return type;
            foreach (var reachable in PublicSignatureTypes(type)
                .SelectMany(ExpandType)
                .Where(candidate => candidate.Assembly == agentAssembly
                    && (candidate.IsPublic || candidate.IsNestedPublic)))
            {
                pending.Enqueue(reachable);
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

    private static bool IsForbiddenPublicType(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        return type == typeof(object)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type)
            || typeof(TextWriter).IsAssignableFrom(type)
            || typeof(FileSystemInfo).IsAssignableFrom(type)
            || typeof(IDictionary).IsAssignableFrom(type)
            || type.GetInterfaces().Any(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            || type == typeof(Uri)
            || fullName.StartsWith("System.Net.", StringComparison.Ordinal)
            || fullName.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal)
            || fullName.StartsWith("Microsoft.Build.", StringComparison.Ordinal)
            || fullName.StartsWith("ContractScribe.Roslyn.", StringComparison.Ordinal)
            || fullName.StartsWith("ContractScribe.Patching.", StringComparison.Ordinal)
            || fullName.StartsWith("ContractScribe.Cli.", StringComparison.Ordinal)
            || fullName.Contains("GitHub", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> FindForbiddenCapabilities(
        IReadOnlyDictionary<string, string> sources)
    {
        const string globalUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.Globalization;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;
        var trees = sources.Select(pair => CSharpSyntaxTree.ParseText(
                pair.Value,
                new CSharpParseOptions(LanguageVersion.Latest),
                pair.Key))
            .Append(CSharpSyntaxTree.ParseText(
                globalUsings,
                new CSharpParseOptions(LanguageVersion.Latest),
                "GlobalUsings.g.cs"))
            .ToArray();
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .GroupBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "DocumentationScribeCapabilityInspection",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var findings = new List<string>();
        foreach (var tree in trees.Where(tree => tree.FilePath != "GlobalUsings.g.cs"))
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var node in tree.GetRoot().DescendantNodesAndSelf())
            {
                var symbol = model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol;
                if (IsForbiddenMember(symbol))
                {
                    var memberLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add($"{tree.FilePath}:{memberLine}:{symbol!.ToDisplayString()}");
                    continue;
                }

                var types = SymbolTypes(symbol)
                    .Append(model.GetTypeInfo(node).Type)
                    .Where(type => type is not null)
                    .Select(type => type!)
                    .ToArray();
                var rejectDelegate = node is TypeSyntax or DelegateDeclarationSyntax;
                var forbidden = types.FirstOrDefault(type => ContainsForbiddenType(type, rejectDelegate));
                if (forbidden is null)
                {
                    continue;
                }

                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add($"{tree.FilePath}:{line}:{forbidden.ToDisplayString()}");
            }
        }

        return findings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<ITypeSymbol?> SymbolTypes(ISymbol? symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                yield return method.ContainingType;
                yield return method.ReturnType;
                break;
            case IPropertySymbol property:
                yield return property.ContainingType;
                yield return property.Type;
                break;
            case IFieldSymbol field:
                yield return field.ContainingType;
                yield return field.Type;
                break;
            case IEventSymbol eventSymbol:
                yield return eventSymbol.ContainingType;
                yield return eventSymbol.Type;
                break;
            case IParameterSymbol parameter:
                yield return parameter.Type;
                break;
            case INamedTypeSymbol named:
                yield return named;
                break;
        }
    }

    private static bool ContainsForbiddenType(ITypeSymbol type, bool rejectDelegate)
    {
        if (type is IArrayTypeSymbol array)
        {
            return ContainsForbiddenType(array.ElementType, rejectDelegate);
        }

        if (type is INamedTypeSymbol named)
        {
            var name = named.OriginalDefinition.ToDisplayString();
            if (rejectDelegate && named.TypeKind == TypeKind.Delegate
                || name is "System.IServiceProvider" or "System.Uri" or "System.Environment"
                || name.StartsWith("System.IO.", StringComparison.Ordinal)
                || name.StartsWith("System.Net.", StringComparison.Ordinal)
                || name.StartsWith("System.Diagnostics.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Build.", StringComparison.Ordinal)
                || name.StartsWith("ContractScribe.Roslyn.", StringComparison.Ordinal)
                || name.StartsWith("ContractScribe.Patching.", StringComparison.Ordinal)
                || name.StartsWith("ContractScribe.Cli.", StringComparison.Ordinal)
                || name.Contains("GitHub", StringComparison.Ordinal)
                || name is "System.Collections.IDictionary"
                    or "System.Collections.Generic.IDictionary<TKey, TValue>"
                    or "System.Collections.Generic.Dictionary<TKey, TValue>")
            {
                return true;
            }

            return named.TypeArguments.Any(argument => ContainsForbiddenType(argument, rejectDelegate));
        }

        return false;
    }

    private static bool IsForbiddenMember(ISymbol? symbol)
    {
        if (symbol is not IPropertySymbol property)
        {
            return false;
        }

        var containingType = property.ContainingType.ToDisplayString();
        return containingType is "System.DateTime" or "System.DateTimeOffset"
                && property.Name is "Now" or "UtcNow"
            || containingType == "System.TimeZoneInfo" && property.Name == "Local"
            || containingType == "System.Globalization.CultureInfo"
                && property.Name is "CurrentCulture" or "CurrentUICulture"
                    or "DefaultThreadCurrentCulture" or "DefaultThreadCurrentUICulture"
            || containingType == "System.Threading.Thread"
                && property.Name is "CurrentCulture" or "CurrentUICulture";
    }

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
