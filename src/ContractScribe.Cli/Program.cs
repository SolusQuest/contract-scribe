using System.Reflection;
using System.Runtime.InteropServices;
using ContractScribe.Core;

var command = args.SingleOrDefault();

return command switch
{
    null or "--help" or "-h" => ShowHelp(),
    "--version" or "-v" => ShowVersion(),
    "doctor" => ShowDoctor(),
    _ => ShowUnknownCommand(command),
};

static int ShowHelp()
{
    Console.WriteLine("ContractScribe bootstrap CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  contract-scribe [--help] [--version] [doctor]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  doctor      Print local runtime diagnostics without network or token access.");
    return 0;
}

static int ShowVersion()
{
    Console.WriteLine($"{ProductInfo.Name} {GetApplicationVersion()}");
    return 0;
}

static int ShowDoctor()
{
    Console.WriteLine($"application_version: {GetApplicationVersion()}");
    Console.WriteLine($"runtime_version: {Environment.Version}");
    Console.WriteLine($"runtime_identifier: {RuntimeInformation.RuntimeIdentifier}");
    Console.WriteLine("network_access: not performed");
    Console.WriteLine("token_access: not performed");
    return 0;
}

static int ShowUnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    Console.Error.WriteLine("Run 'contract-scribe --help' for usage.");
    return 2;
}

static string GetApplicationVersion()
{
    return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
