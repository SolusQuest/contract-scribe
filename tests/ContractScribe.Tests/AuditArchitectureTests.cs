using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractScribe.Tests;

public sealed class AuditArchitectureTests
{
    [Fact]
    public void ProductionAuditImplementation_HasNoForbiddenInfrastructureDependencies()
    {
        var coreDirectory = Path.Join(
            AuditResultConformance.FindRepositoryRoot(),
            "src",
            "ContractScribe.Core");
        var sources = Directory.EnumerateFiles(
                coreDirectory,
                "Audit*.cs",
                SearchOption.TopDirectoryOnly)
            .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        Assert.NotEmpty(sources);
        var findings = FindForbiddenSymbols(sources);
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    [Theory]
    [InlineData("class C { string M() => System.Environment.NewLine; }")]
    [InlineData("class C { string M() => System.IO.File.ReadAllText(\"schema.json\"); }")]
    [InlineData("class C { void M() => System.IO.File.WriteAllText(\"result.json\", \"x\"); }")]
    [InlineData("class C { object? M() => System.Diagnostics.Process.Start(\"tool\"); }")]
    [InlineData("class C { string M(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture); }")]
    [InlineData("class C { string M() => System.IO.Directory.GetCurrentDirectory(); }")]
    public void Analyzer_RejectsForbiddenCallsEvenInsideHelpersAndInitializers(string source)
    {
        var findings = FindForbiddenSymbols(new Dictionary<string, string>
        {
            ["Synthetic.cs"] = source,
        });

        Assert.NotEmpty(findings);
    }

    private static IReadOnlyList<string> FindForbiddenSymbols(
        IReadOnlyDictionary<string, string> sources)
    {
        var trees = sources.Select(pair => CSharpSyntaxTree.ParseText(
                pair.Value,
                path: pair.Key,
                options: new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .GroupBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "AuditArchitectureInspection",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var findings = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var node in tree.GetRoot().DescendantNodesAndSelf())
            {
                if (node is not (InvocationExpressionSyntax
                    or ObjectCreationExpressionSyntax
                    or MemberAccessExpressionSyntax
                    or IdentifierNameSyntax))
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(node).Symbol;
                var type = symbol switch
                {
                    IMethodSymbol method => method.ContainingType,
                    IPropertySymbol property => property.ContainingType,
                    IFieldSymbol field => field.ContainingType,
                    INamedTypeSymbol named => named,
                    _ => model.GetTypeInfo(node).Type,
                };
                if (symbol is null || type is null)
                {
                    continue;
                }

                var typeName = type.ToDisplayString();
                var memberName = symbol.Name;
                if (IsForbidden(typeName, memberName, symbol))
                {
                    findings.Add($"{tree.FilePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}:{typeName}.{memberName}");
                }
            }
        }

        return findings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsForbidden(string typeName, string memberName, ISymbol symbol)
    {
        if (typeName == "System.Environment"
            || typeName is "System.IO.File" or "System.IO.Directory"
            || typeName == "System.Diagnostics.Process"
            || typeName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.Build", StringComparison.Ordinal)
            || typeName.StartsWith("ContractScribe.Roslyn", StringComparison.Ordinal)
            || typeName.StartsWith("ContractScribe.Cli", StringComparison.Ordinal)
            || typeName.Contains("GitHub", StringComparison.Ordinal)
            || typeName.Contains("Provider", StringComparison.Ordinal))
        {
            return true;
        }

        if (typeName is "System.DateTime" or "System.DateTimeOffset"
            && memberName is "Now" or "UtcNow")
        {
            return true;
        }

        if (typeName == "System.TimeZoneInfo" && memberName == "Local")
        {
            return true;
        }

        if (typeName == "System.Globalization.CultureInfo"
            && memberName is "CurrentCulture" or "CurrentUICulture"
                or "DefaultThreadCurrentCulture" or "DefaultThreadCurrentUICulture")
        {
            return true;
        }

        return symbol is IMethodSymbol method
            && typeName is "int" or "long" or "short" or "decimal" or "double" or "float"
            && memberName == "ToString"
            && !method.Parameters.Any(parameter =>
                parameter.Type.ToDisplayString().StartsWith(
                    "System.IFormatProvider",
                    StringComparison.Ordinal));
    }
}
