using System.Xml.Linq;

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
    public void HistoricalAndProductionRoslynProjectsUseSeparateTestHosts()
    {
        var root = FindRepositoryRoot();
        var fast = File.ReadAllText(Path.Combine(root, "tests", "ContractScribe.Tests", "ContractScribe.Tests.csproj"));
        var integration = File.ReadAllText(Path.Combine(root, "tests", "ContractScribe.IntegrationTests", "ContractScribe.IntegrationTests.csproj"));
        Assert.Contains(@"..\ContractScribe.Roslyn\ContractScribe.Roslyn.csproj", fast, StringComparison.Ordinal);
        Assert.DoesNotContain(@"src\ContractScribe.Roslyn", fast, StringComparison.Ordinal);
        Assert.Contains(@"../../src/ContractScribe.Roslyn/ContractScribe.Roslyn.csproj", integration, StringComparison.Ordinal);
        Assert.DoesNotContain(@"tests/ContractScribe.Roslyn", integration, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.StringTools", integration, StringComparison.Ordinal);
        Assert.Equal(5, XDocument.Parse(integration).Descendants("PackageReference").Count(element =>
            element.Attribute("ExcludeAssets")?.Value == "runtime"));
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
