using System.Diagnostics;
using System.Security;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.IntegrationTests;

public sealed class AuditProcessDeterminismTests
{
    [Fact]
    public async Task CanonicalBytes_AreStableAcrossFreshCultureAndTimeZoneProcesses()
    {
        var repositoryRoot = FindRepositoryRoot();
        var canonical = File.ReadAllBytes(Path.Join(
            repositoryRoot,
            "tests",
            "fixtures",
            "audit-result",
            "v1",
            "golden",
            "required-present.canonical.json"));
        var temporaryRoot = Path.Join(
            Path.GetTempPath(),
            "contractscribe-audit-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var coreProject = Path.Join(
                repositoryRoot,
                "src",
                "ContractScribe.Core",
                "ContractScribe.Core.csproj");
            File.WriteAllText(
                Path.Join(temporaryRoot, "Probe.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{SecurityElement.Escape(coreProject)}}" />
                  </ItemGroup>
                </Project>
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                Path.Join(temporaryRoot, "Program.cs"),
                """
                using System.Globalization;
                using ContractScribe.Core;

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(args[0]);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(args[0]);
                var bytes = Convert.FromBase64String(args[1]);
                var document = AuditParser.Promote(
                    AuditParser.Parse(bytes),
                    new Dictionary<AuditEvidenceKey, string>());
                Console.Write(Convert.ToBase64String(AuditJson.Write(document)));
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var payload = Convert.ToBase64String(canonical);
            var first = await RunProbe(temporaryRoot, "tr-TR", "Pacific/Kiritimati", payload);
            var second = await RunProbe(temporaryRoot, "fr-FR", "America/Los_Angeles", payload);

            Assert.Equal(payload, first);
            Assert.Equal(first, second);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<string> RunProbe(
        string projectRoot,
        string culture,
        string timeZone,
        string payload)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project Probe.csproj -c Release -- {culture} {payload}",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["TZ"] = timeZone;
        process.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
        return (await stdout).Trim();
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
            ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
