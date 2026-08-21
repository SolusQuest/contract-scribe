namespace ContractScribe.Evaluation;

internal static class Program
{
    public static async Task<int> Main(string[] args) =>
        await EvaluationApplication.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
}
