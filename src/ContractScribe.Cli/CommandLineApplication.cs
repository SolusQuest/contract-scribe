using System.Reflection;
using System.Runtime.InteropServices;
using ContractScribe.Core;

namespace ContractScribe.Cli;

/// <summary>
/// Implements the intentionally narrow bootstrap command surface.
/// </summary>
public static class CommandLineApplication
{
    /// <summary>
    /// Gets the informational version from the command-line assembly metadata.
    /// </summary>
    public static string ApplicationVersion { get; } = GetApplicationVersion();

    /// <summary>
    /// Executes a supported command and writes only its documented output.
    /// </summary>
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length > 1)
        {
            return ShowUnknownCommand(string.Join(' ', args), error);
        }

        var command = args.SingleOrDefault();
        return command switch
        {
            null or "--help" or "-h" => ShowHelp(output),
            "--version" or "-v" => ShowVersion(output),
            "doctor" => ShowDoctor(output),
            _ => ShowUnknownCommand(command, error),
        };
    }

    private static int ShowHelp(TextWriter output)
    {
        output.WriteLine("ContractScribe bootstrap CLI");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  contract-scribe [--help] [--version] [doctor]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  doctor      Print an allowlisted local runtime diagnostic without network or credential access.");
        return 0;
    }

    private static int ShowVersion(TextWriter output)
    {
        output.WriteLine($"{ProductInfo.Name} {ApplicationVersion}");
        return 0;
    }

    private static int ShowDoctor(TextWriter output)
    {
        output.WriteLine($"application_version: {ApplicationVersion}");
        output.WriteLine($"runtime_description: {RuntimeInformation.FrameworkDescription}");
        output.WriteLine($"process_architecture: {RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"runtime_identifier: {RuntimeInformation.RuntimeIdentifier}");
        output.WriteLine("network_access: not performed");
        output.WriteLine("credential_access: not performed");
        return 0;
    }

    private static int ShowUnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command: {command}");
        error.WriteLine("Run 'contract-scribe --help' for usage.");
        return 2;
    }

    private static string GetApplicationVersion()
    {
        return typeof(CommandLineApplication)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
    }
}
