using ContractScribe.Cli;
using ContractScribe.Roslyn;

if (HostValidationSubjectAdapter.IsEnabled
    && args is ["--request", var requestPath, "--response", var responsePath])
{
    return await HostValidationSubjectAdapter.RunAsync(requestPath, responsePath)
        .ConfigureAwait(false);
}
return CommandLineApplication.Execute(args, Console.Out, Console.Error);
