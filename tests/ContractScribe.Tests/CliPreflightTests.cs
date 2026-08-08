using System.Diagnostics;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class CliPreflightTests
{
    [Fact]
    public void Run_ResolvesRelativeInputsAndExternalOutput()
    {
        using var fixture = new PreflightFixture();

        var result = CliPreflight.Run(
            new AuditCommandArguments(
                "repo",
                "src/App.CSPROJ",
                "policy.json",
                "outside/result.json"),
            fixture.Root);

        Assert.Equal(Path.Join(fixture.Repository, "src", "App.CSPROJ"), result.InputPath);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(result.PolicyBytes));
        Assert.Equal(Path.Join(fixture.Outside, "result.json"), result.PublicationTarget.FinalPath);
    }

    [Theory]
    [InlineData("missing", "src/App.csproj", "policy.json", "outside/result.json", "cli.preflight.repository-root")]
    [InlineData("repo", "../outside/missing.csproj", "policy.json", "outside/result.json", "cli.preflight.input-escape")]
    [InlineData("repo", "src/App.fsproj", "policy.json", "outside/result.json", "cli.preflight.input")]
    [InlineData("repo", "src/App.CSPROJ", "../outside/missing.json", "outside/result.json", "cli.preflight.policy-escape")]
    [InlineData("repo", "src/App.CSPROJ", "policy.json", "repo/result.json", "cli.preflight.output-inside-root")]
    [InlineData("repo", "src/App.CSPROJ", "policy.json", "missing/result.json", "cli.preflight.output-parent")]
    public void Run_SelectsTheExpectedPreflightCode(
        string root,
        string input,
        string policy,
        string output,
        string expectedCode)
    {
        using var fixture = new PreflightFixture();

        var exception = Assert.Throws<CliPreflightException>(() =>
            CliPreflight.Run(
                new AuditCommandArguments(root, input, policy, output),
                fixture.Root));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Run_RejectsAnInputWhoseExistingAncestorResolvesOutsideRoot()
    {
        using var fixture = new PreflightFixture();
        File.WriteAllText(Path.Join(fixture.Outside, "Outside.csproj"), "<Project />");
        fixture.CreateDirectoryLink(
            Path.Join(fixture.Repository, "linked-outside"),
            fixture.Outside);

        var exception = Assert.Throws<CliPreflightException>(() =>
            CliPreflight.Run(
                new AuditCommandArguments(
                    "repo",
                    "linked-outside/Outside.csproj",
                    "policy.json",
                    "outside/result.json"),
                fixture.Root));

        Assert.Equal("cli.preflight.input-escape", exception.Code);
    }

    [Fact]
    public void Run_RejectsAnOutsideOutputWhoseParentResolvesIntoRoot()
    {
        using var fixture = new PreflightFixture();
        fixture.CreateDirectoryLink(
            Path.Join(fixture.Outside, "linked-into-root"),
            Path.Join(fixture.Repository, "src"));

        var exception = Assert.Throws<CliPreflightException>(() =>
            CliPreflight.Run(
                new AuditCommandArguments(
                    "repo",
                    "src/App.CSPROJ",
                    "policy.json",
                    "outside/linked-into-root/result.json"),
                fixture.Root));

        Assert.Equal("cli.preflight.output-inside-root", exception.Code);
    }

    [Fact]
    public void Run_AcceptsALexicallyInsideOutputWhoseParentResolvesOutsideRoot()
    {
        using var fixture = new PreflightFixture();
        fixture.CreateDirectoryLink(
            Path.Join(fixture.Repository, "linked-outside"),
            fixture.Outside);

        var result = CliPreflight.Run(
            new AuditCommandArguments(
                "repo",
                "src/App.CSPROJ",
                "policy.json",
                "repo/linked-outside/result.json"),
            fixture.Root);

        Assert.Equal(
            Path.Join(fixture.Outside, "result.json"),
            result.PublicationTarget.FinalPath);
    }

    [Fact]
    public void Run_RejectsAReparsePointAtTheFinalOutputComponent()
    {
        using var fixture = new PreflightFixture();
        var target = Path.Join(fixture.Outside, "target-directory");
        Directory.CreateDirectory(target);
        fixture.CreateDirectoryLink(Path.Join(fixture.Outside, "result.json"), target);

        var exception = Assert.Throws<CliPreflightException>(() =>
            CliPreflight.Run(
                new AuditCommandArguments(
                    "repo",
                    "src/App.CSPROJ",
                    "policy.json",
                    "outside/result.json"),
                fixture.Root));

        Assert.Equal("cli.preflight.output-reparse", exception.Code);
    }

    private sealed class PreflightFixture : IDisposable
    {
        private readonly List<string> directoryLinks = [];

        public PreflightFixture()
        {
            Root = Path.Join(Path.GetTempPath(), "contract-scribe-cli-preflight-" + Guid.NewGuid().ToString("N"));
            Repository = Path.Join(Root, "repo");
            Outside = Path.Join(Root, "outside");
            Directory.CreateDirectory(Path.Join(Repository, "src"));
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Join(Repository, "src", "App.CSPROJ"), "<Project />");
            File.WriteAllText(Path.Join(Repository, "src", "App.fsproj"), "<Project />");
            File.WriteAllText(Path.Join(Repository, "policy.json"), "{}");
        }

        public string Root { get; }
        public string Repository { get; }
        public string Outside { get; }

        public void CreateDirectoryLink(string link, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("mklink");
                startInfo.ArgumentList.Add("/J");
                startInfo.ArgumentList.Add(link);
                startInfo.ArgumentList.Add(target);
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Junction setup failed.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Junction setup failed: {process.StandardError.ReadToEnd()}");
                }
            }
            directoryLinks.Add(link);
        }

        public void Dispose()
        {
            foreach (var link in directoryLinks.AsEnumerable().Reverse())
            {
                Directory.Delete(link);
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
