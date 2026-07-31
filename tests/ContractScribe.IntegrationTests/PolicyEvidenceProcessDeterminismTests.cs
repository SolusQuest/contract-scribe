using System.Diagnostics;
using System.Text.Json;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class PolicyEvidenceProcessDeterminismTests
{
    [Fact]
    public async Task NormalizedPolicyEvidenceBytes_AreStableAcrossFreshProcesses()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "App", "App.cs"),
            """
            /// <summary>Café 😀.</summary>
            public class Café
            {
                public void Missing(string value) { }
            }
            """);

        var first = await RunProbeAsync(fixture.Root, "zh-CN", "UTC");
        var second = await RunProbeAsync(
            fixture.Root,
            "tr-TR",
            "Pacific Standard Time");

        Assert.Equal(first, second);
        Assert.DoesNotContain('\n', first);
        Assert.DoesNotContain('\r', first);
        Assert.Contains("\"expectation\":\"required\"", first, StringComparison.Ordinal);
        Assert.Contains(
            "\"kind\":\"evidence.source.declaration\"",
            first,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"kind\":\"evidence.source.xml-documentation\"",
            first,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(first);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.NotEqual(0, document.RootElement.GetArrayLength());
    }

    private static async Task<string> RunProbeAsync(
        string repositoryRoot,
        string culture,
        string timeZone)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(LoaderProbePath());
        startInfo.ArgumentList.Add(repositoryRoot);
        startInfo.ArgumentList.Add("App/App.csproj");
        startInfo.ArgumentList.Add("policy-evidence");
        startInfo.ArgumentList.Add(culture);
        startInfo.ArgumentList.Add(timeZone);
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Policy/evidence probe failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        Assert.True(
            process.ExitCode == 0,
            $"Policy/evidence probe failed with exit {process.ExitCode}:{Environment.NewLine}{output}{Environment.NewLine}{error}");
        return output;
    }

    private static string LoaderProbePath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ContractScribe.LoaderProbe",
            "bin",
            configuration,
            "net10.0",
            "ContractScribe.LoaderProbe.dll");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException("The loader probe was not built.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
