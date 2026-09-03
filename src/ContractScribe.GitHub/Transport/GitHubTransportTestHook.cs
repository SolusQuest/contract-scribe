namespace ContractScribe.GitHub.Transport;

// H2's process startup hook may register this by reflection. There is no product
// caller, environment lookup, configuration option, or public handler overload.
internal static class GitHubTransportTestHook
{
    internal const string Placeholder = "contract-scribe-synthetic-transport-only";
    private static readonly object Gate = new();
    private static Registration? current;

    private static IDisposable Register(Uri endpoint, HttpMessageHandler? scriptedHandler = null,
        int requestTimeoutMilliseconds = 30_000)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "http"
            || endpoint.IsDefaultPort || endpoint.Port <= 0 || endpoint.AbsolutePath != "/"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.OriginalString != endpoint.AbsoluteUri
            || endpoint.GetComponents(UriComponents.Host, UriFormat.UriEscaped) is not ("127.0.0.1" or "[::1]")
            || requestTimeoutMilliseconds is < 1 or > 30_000)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        lock (Gate)
        {
            if (current is not null) throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
            current = new(endpoint, scriptedHandler, requestTimeoutMilliseconds);
            return current;
        }
    }

    internal static (Uri Endpoint, HttpMessageHandler? Handler, int TimeoutMilliseconds)? Take(string credential)
    {
        lock (Gate)
        {
            if (current is null)
            {
                if (credential == Placeholder) throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
                return null;
            }
            if (credential != Placeholder || current.Taken)
                throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
            current.Taken = true;
            return (current.Endpoint, current.Handler, current.TimeoutMilliseconds);
        }
    }

    private sealed class Registration(Uri endpoint, HttpMessageHandler? handler, int timeoutMilliseconds) : IDisposable
    {
        internal Uri Endpoint { get; } = endpoint;
        internal HttpMessageHandler? Handler { get; } = handler;
        internal int TimeoutMilliseconds { get; } = timeoutMilliseconds;
        internal bool Taken { get; set; }

        public void Dispose()
        {
            lock (Gate)
            {
                if (ReferenceEquals(current, this))
                {
                    current = null;
                    if (!Taken) Handler?.Dispose();
                }
            }
        }
    }
}
