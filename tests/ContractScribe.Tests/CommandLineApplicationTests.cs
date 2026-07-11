using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class CommandLineApplicationTests
{
    [Fact]
    public void Help_ExitsSuccessfully_AndWritesUsageToStandardOutput()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(["--help"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Doctor_UsesOnlyTheAllowlistedDiagnosticFields()
    {
        var output = new StringWriter();
        var error = new StringWriter();

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
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2)[0]));
    }

    [Fact]
    public void Version_UsesTheSameAssemblyMetadataAsDoctor()
    {
        var versionOutput = new StringWriter();
        var versionError = new StringWriter();
        var doctorOutput = new StringWriter();
        var doctorError = new StringWriter();

        var versionExitCode = CommandLineApplication.Execute(["--version"], versionOutput, versionError);
        var doctorExitCode = CommandLineApplication.Execute(["doctor"], doctorOutput, doctorError);

        Assert.Equal(0, versionExitCode);
        Assert.Equal(0, doctorExitCode);
        Assert.Empty(versionError.ToString());
        Assert.Empty(doctorError.ToString());
        Assert.NotEqual("unknown", CommandLineApplication.ApplicationVersion);
        Assert.Equal($"ContractScribe {CommandLineApplication.ApplicationVersion}{Environment.NewLine}", versionOutput.ToString());
        Assert.Contains($"application_version: {CommandLineApplication.ApplicationVersion}", doctorOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommand_Fails_AndWritesOnlyToStandardError()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandLineApplication.Execute(["audit"], output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Unknown command: audit", error.ToString(), StringComparison.Ordinal);
    }
}
