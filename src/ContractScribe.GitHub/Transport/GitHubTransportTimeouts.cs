using ContractScribe.Core;

namespace ContractScribe.GitHub.Transport;

internal sealed record GitHubTransportTimeouts
{
    private GitHubTransportTimeouts(int request, int connect)
    { RequestMilliseconds = request; ConnectMilliseconds = connect; }

    internal int RequestMilliseconds { get; }
    internal int ConnectMilliseconds { get; }
    internal static GitHubTransportTimeouts Default { get; } = Create();

    internal static GitHubTransportTimeouts Create(int requestMilliseconds = 30_000, int connectMilliseconds = 10_000)
    {
        if (requestMilliseconds is < 1 or > DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds
            || connectMilliseconds < 1 || connectMilliseconds > requestMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(requestMilliseconds));
        return new(requestMilliseconds, connectMilliseconds);
    }
}
