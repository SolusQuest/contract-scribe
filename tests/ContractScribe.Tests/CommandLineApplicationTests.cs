using System.Text.RegularExpressions;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class CommandLineApplicationTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Theory]
    [InlineData(null, "help-top-level.txt")]
    [InlineData("--help", "help-top-level.txt")]
    [InlineData("-h", "help-top-level.txt")]
    [InlineData("audit --help", "help-audit.txt")]
    [InlineData("audit -h", "help-audit.txt")]
    public void Help_IsTheExactPinnedLfFixture(string? command, string fixture)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = command?.Split(' ') ?? [];

        var exitCode = CommandLineApplication.Execute(args, output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            File.ReadAllText(Path.Join(
                Root,
                "tests",
                "fixtures",
                "m1-audit-cli",
                fixture)),
            output.ToString());
        Assert.DoesNotContain('\r', output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Doctor_UsesOnlyTheAllowlistedDiagnosticFields()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(["doctor"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(
            [
                "application_version",
                "runtime_description",
                "process_architecture",
                "runtime_identifier",
                "network_access",
                "credential_access",
            ],
            output.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2)[0]));
        Assert.EndsWith(
            "network_access: not performed\ncredential_access: not performed\n",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Version_UsesExactRevisionBearingAssemblyMetadata()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(["--version"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Matches(
            new Regex("^ContractScribe .+\\+[0-9a-f]{40}\\n$", RegexOptions.CultureInvariant),
            output.ToString());
        Assert.Equal(
            $"ContractScribe {CommandLineApplication.ApplicationVersion}\n",
            output.ToString());
    }

    [Theory]
    [InlineData("bogus", "cli.usage.unknown-command")]
    [InlineData("--help audit", "cli.usage.forbidden-combination")]
    [InlineData("--version audit", "cli.usage.forbidden-combination")]
    [InlineData("doctor anything", "cli.usage.forbidden-combination")]
    public void TopLevelUsageFailure_WritesOnlyOneBoundedStderrRecord(
        string command,
        string code)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(command.Split(' '), output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith(code + ": ", error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("\n", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(command, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RecognizedAuditUsageFailure_WritesUsageEnvelope()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(["audit"], output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("\"terminalLayer\":\"usage\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"usageClass\":\"missing-required-option\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "cli.usage.missing-required-option: a required option is missing\n",
            error.ToString());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
