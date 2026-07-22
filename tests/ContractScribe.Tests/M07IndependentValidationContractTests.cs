using System.Text.Json;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class M07IndependentValidationContractTests
{
    [Fact]
    public void ValidationManifestMatchesItsVersionedSchema()
    {
        var root = FindRepositoryRoot();
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "schemas", "experiments", "m0.7-independent-validation-v1.schema.json")));
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "m0.7-independent-validation-manifest.json")));

        var schema = JsonSchema.FromText(schemaDocument.RootElement.GetRawText());
        var result = schema.Evaluate(manifestDocument.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors?.Select(pair => $"{pair.Key}: {pair.Value}") ?? Array.Empty<string>()));
    }

    [Fact]
    public void ValidationManifestPinsTheExternalFixtureAndOracle()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "m0.7-independent-validation-manifest.json")));
        var fixture = document.RootElement.GetProperty("fixture");

        Assert.Equal("https://github.com/Yuee98/contract-scribe-m07-fixture", fixture.GetProperty("repository").GetString());
        Assert.Equal("https://github.com/Yuee98/contract-scribe-m07-fixture/issues/1", fixture.GetProperty("trackingIssue").GetString());
        Assert.Matches("^[0-9a-f]{40}$", fixture.GetProperty("commit").GetString()!);
        Assert.Equal("expected-payload.json", fixture.GetProperty("oraclePath").GetString());
        Assert.Matches("^[0-9a-f]{64}$", fixture.GetProperty("oracleSha256").GetString()!);
        Assert.DoesNotContain("TO_BE_FILLED", File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "m0.7-independent-validation-manifest.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationManifestClosesMatrixComparisonAndOutcomeRules()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "m0.7-independent-validation-manifest.json")));
        var matrix = document.RootElement.GetProperty("matrix");
        var comparison = document.RootElement.GetProperty("comparison");
        var outcomes = document.RootElement.GetProperty("outcomeTaxonomy");

        Assert.Equal(new[] { "ubuntu-latest", "windows-latest" }, matrix.GetProperty("operatingSystems").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("X64", matrix.GetProperty("processArchitecture").GetString());
        Assert.Equal(2, comparison.GetProperty("freshProcessCount").GetInt32());
        Assert.True(comparison.GetProperty("crossRunEquality").GetBoolean());
        Assert.True(comparison.GetProperty("crossCellEquality").GetBoolean());
        Assert.Equal(new[] { "protocol-failure", "baseline-invalidated", "baseline-failure", "inconclusive", "succeeded" }, outcomes.GetProperty("aggregatePrecedence").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
