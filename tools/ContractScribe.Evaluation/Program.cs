namespace ContractScribe.Evaluation;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        IDisposable signals;
        try
        {
            signals = ProductionCompositionAdapter.InstallSignals(cancellation);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            await Console.Error.WriteLineAsync("evaluation.signal-registration-failed").ConfigureAwait(false);
            return 1;
        }

        using (signals)
        {
            return await EvaluationApplication.RunAsync(
                args,
                Console.Out,
                Console.Error,
                cancellation.Token).ConfigureAwait(false);
        }
    }
}
