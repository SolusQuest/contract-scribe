namespace ContractScribe.GitHub.Transport;

// A successful write is not proof of visibility on a subsequent read. Owners
// use this only after one dispatched mutation, and still authenticate the
// returned object themselves. Never replay the mutation or retry a conflict.
internal static class GitHubObjectReadback
{
    internal const int MaximumObservations = 4;

    internal static async ValueTask<GitHubApiResult<T>> ObserveAsync<T>(
        Func<CancellationToken, ValueTask<GitHubApiResult<T>>> read,
        CancellationToken recovery) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await read(recovery).ConfigureAwait(false);
            if (result.Failure?.Code != GitHubFailureCode.NotFound
                || attempt == MaximumObservations - 1 || recovery.IsCancellationRequested)
                return result;
            try { await Task.Delay(250 << attempt, recovery).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return GitHubApiResult<T>.Failed(GitHubFailureCode.Timeout, dispatched: true);
            }
        }
    }
}
