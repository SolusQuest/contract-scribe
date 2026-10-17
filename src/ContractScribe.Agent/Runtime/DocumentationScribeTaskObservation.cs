namespace ContractScribe.Agent.Runtime;

internal static class DocumentationScribeTaskObservation
{
    internal static async Task ObserveLateAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
    }
}
