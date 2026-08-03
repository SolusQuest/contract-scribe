using System.Reflection;
using ContractScribe.Cli;
using ContractScribe.Roslyn;

var cliAssembly = Assembly.GetExecutingAssembly();
if (HostValidationSubjectAdapter.IsEnabledFor(cliAssembly)
    && args is ["--request", var requestPath, "--response", var responsePath])
{
    return await HostValidationSubjectAdapter.RunAsync(requestPath, responsePath, cliAssembly)
        .ConfigureAwait(false);
}
return CommandLineApplication.Execute(args, Console.Out, Console.Error);
