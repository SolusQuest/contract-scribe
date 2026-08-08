using System.Reflection;
using System.Runtime.InteropServices;
using ContractScribe.Core;

namespace ContractScribe.Cli;

/// <summary>
/// Implements the closed ContractScribe command surface.
/// </summary>
public static class CommandLineApplication
{
    private const string TopLevelHelp =
        "ContractScribe CLI\n" +
        "\n" +
        "Usage:\n" +
        "  contract-scribe [--help | --version | doctor]\n" +
        "  contract-scribe audit --repository-root <path> --input <path> --policy <path> --output <path>\n" +
        "\n" +
        "Commands:\n" +
        "  audit       Run the deterministic XML documentation audit.\n" +
        "  doctor      Print an allowlisted local runtime diagnostic without network or credential access.\n" +
        "\n" +
        "Options:\n" +
        "  -h, --help      Print this help.\n" +
        "  -v, --version   Print the tool version.\n";

    private const string AuditHelp =
        "ContractScribe audit\n" +
        "\n" +
        "Usage:\n" +
        "  contract-scribe audit --repository-root <path> --input <path> --policy <path> --output <path>\n" +
        "\n" +
        "Options:\n" +
        "  --repository-root <path>  Repository root directory. Must exist.\n" +
        "  --input <path>            Audit input (.sln, .slnx, or .csproj). Must resolve inside the repository root.\n" +
        "  --policy <path>           Policy configuration file. Must resolve inside the repository root.\n" +
        "  --output <path>           Audit result file. Must resolve outside the repository root.\n" +
        "  -h, --help                Print this help.\n" +
        "\n" +
        "All four path options are required, take exactly one value, and may appear in any order. Both \"--option value\" and \"--option=value\" forms are accepted.\n" +
        "\n" +
        "Exit codes:\n" +
        "  0  No violations (also help, version, and doctor).\n" +
        "  1  Documentation violations found.\n" +
        "  2  Invalid command-line usage.\n" +
        "  3  No audit judgments (no results, or every result skipped).\n" +
        "  4  Invalid input or unavailable environment.\n" +
        "  5  Load, audit, or publication failure.\n" +
        "  6  Cancelled.\n" +
        "  7  Timeout.\n";

    /// <summary>
    /// Gets the informational version from the command-line assembly metadata.
    /// </summary>
    public static string ApplicationVersion { get; } = GetApplicationVersion();

    /// <summary>
    /// Executes a supported command and writes only its documented output.
    /// </summary>
    public static int Execute(string[] args, TextWriter output, TextWriter error) =>
        ExecuteAsync(args, output, error, installSignalHandlers: false)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Executes the process command surface, including audit-only signal handling.
    /// </summary>
    public static Task<int> ExecuteProcessAsync(
        string[] args,
        TextWriter output,
        TextWriter error) =>
        ExecuteAsync(args, output, error, installSignalHandlers: true);

    internal static Task<int> ExecuteProcessAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Action<IDisposable> retainHandledSignalRegistration) =>
        ExecuteAsync(
            args,
            output,
            error,
            installSignalHandlers: true,
            retainHandledSignalRegistration: retainHandledSignalRegistration);

    internal static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        bool installSignalHandlers,
        string? currentDirectory = null,
        CancellationToken cancellationToken = default,
        Action<IDisposable>? retainHandledSignalRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            output.Write(TopLevelHelp);
            return 0;
        }

        if (args.Length == 1)
        {
            switch (args[0])
            {
                case "--help":
                case "-h":
                    output.Write(TopLevelHelp);
                    return 0;
                case "--version":
                case "-v":
                    output.Write($"{ProductInfo.Name} {ApplicationVersion}\n");
                    return 0;
                case "doctor":
                    WriteDoctor(output);
                    return 0;
            }
        }

        if (!string.Equals(args[0], "audit", StringComparison.Ordinal))
        {
            var code = args[0] is "--help" or "-h" or "--version" or "-v" or "doctor"
                ? "cli.usage.forbidden-combination"
                : "cli.usage.unknown-command";
            CliDiagnostics.Write(error, code);
            return 2;
        }

        if (args.Length == 2 && args[1] is "--help" or "-h")
        {
            output.Write(AuditHelp);
            return 0;
        }

        var identity = CliBuildIdentity.Current;
        var parsed = AuditCommandParser.Parse(args.AsSpan(1));
        if (parsed.Failure is not null)
        {
            var result = CliPresentation.Usage(identity, parsed.Failure);
            Write(result, output, error);
            return result.ExitCode;
        }

        CliPreflightResult preflight;
        try
        {
            preflight = CliPreflight.Run(
                parsed.Arguments!,
                currentDirectory ?? Environment.CurrentDirectory);
        }
        catch (CliPreflightException exception)
        {
            var result = CliPresentation.Preflight(identity, exception.Code);
            Write(result, output, error);
            return result.ExitCode;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signals = installSignalHandlers
            ? AuditSignalRegistration.Install(linkedCancellation)
            : null;
        try
        {
            var auditResult = await AuditCommandRunner.RunAsync(
                identity,
                preflight,
                linkedCancellation.Token).ConfigureAwait(false);
            Write(auditResult, output, error);
            if (auditResult.ExitCode == 6
                && signals is not null
                && retainHandledSignalRegistration is not null)
            {
                retainHandledSignalRegistration(signals);
                signals = null;
            }
            return auditResult.ExitCode;
        }
        finally
        {
            signals?.Dispose();
        }
    }

    private static void Write(CliExecutionResult result, TextWriter output, TextWriter error)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            error.Write(diagnostic.ToLine());
        }
        output.Write(result.StandardOutput);
    }

    private static void WriteDoctor(TextWriter output)
    {
        output.Write($"application_version: {ApplicationVersion}\n");
        output.Write($"runtime_description: {RuntimeInformation.FrameworkDescription}\n");
        output.Write($"process_architecture: {RuntimeInformation.ProcessArchitecture}\n");
        output.Write($"runtime_identifier: {RuntimeInformation.RuntimeIdentifier}\n");
        output.Write("network_access: not performed\n");
        output.Write("credential_access: not performed\n");
    }

    private static string GetApplicationVersion() =>
        typeof(CommandLineApplication)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? throw new InvalidOperationException(
            "The CLI assembly must define an informational version.");
}
