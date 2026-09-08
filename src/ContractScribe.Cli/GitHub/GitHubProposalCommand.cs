namespace ContractScribe.Cli;

internal static class GitHubProposalCommand
{
    internal const string Help =
        "ContractScribe github-proposal\n\n" +
        "Usage:\n" +
        "  contract-scribe github-proposal start --repository-root <path> --input <path> --policy <path> --snapshot <binding> --state <path> --configuration <path> --github-configuration <path>\n" +
        "  contract-scribe github-proposal resume --repository-root <path> --input <path> --policy <path> --snapshot <binding> --state <path> --configuration <path> --github-configuration <path>\n\n" +
        "Options:\n" +
        "  --repository-root <path>       Existing repository root.\n" +
        "  --input <path>                 Existing .sln, .slnx, or .csproj inside the repository.\n" +
        "  --policy <path>                Existing M1 policy file inside the repository.\n" +
        "  --snapshot <binding>           Caller-attested immutable snapshot binding.\n" +
        "  --state <path>                 Campaign checkpoint outside the repository.\n" +
        "  --configuration <path>         Strict non-secret campaign configuration JSON.\n" +
        "  --github-configuration <path>  Strict non-secret GitHub publication configuration JSON.\n" +
        "  -h, --help                     Print this help.\n\n" +
        "All seven options are required exactly once. Both --option value and --option=value forms are accepted in any order.\n\n" +
        "Environment:\n" +
        "  CONTRACTSCRIBE_PROVIDER_API_KEY  Existing campaign provider credential.\n" +
        "  CONTRACTSCRIBE_GITHUB_TOKEN      GitHub credential read once after local publication admission.\n\n" +
        "Exit codes:\n" +
        "  0  Published, replayed, no work, awaiting review, or merged.\n" +
        "  2  Invalid command-line usage.\n" +
        "  3  Bounded resumable conflict, rate limit, or stale base after creation.\n" +
        "  4  Invalid state or authority, human change, permission failure, or closed unmerged.\n" +
        "  5  Host failure.\n" +
        "  6  Cancelled.\n" +
        "  7  Timeout.\n";

    internal static async Task<CliExecutionResult> RunAsync(GitHubProposalCommandArguments arguments,
        string currentDirectory, CancellationToken cancellationToken, Func<string, string?>? credentialAccessor = null)
    {
        var identity = CliBuildIdentity.Current;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preflight = CampaignPreflight.Run(arguments.Campaign, currentDirectory);
            var github = GitHubProposalConfigurationReader.Read(arguments.GitHubConfiguration, currentDirectory);
            if (GitHubProposalProcessHooks.Terminal(arguments.Operation) is { } selectedTerminal)
                return GitHubProposalPresentation.Campaign(identity, selectedTerminal);
            var accessor = credentialAccessor ?? Environment.GetEnvironmentVariable;
            var continuation = new CampaignAcceptedCandidateContinuation(identity, preflight, github, name =>
            {
                GitHubProposalProcessHooks.Reach("token-read");
                return accessor(name);
            });
            return await CampaignCommandRunner.RunAsync(identity, preflight, cancellationToken,
                accessor, continuation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GitHubProposalPresentation.Local(identity, arguments.Operation, "cancelled");
        }
        catch (Exception exception) when (exception is CampaignPreflightException
            or System.Text.Json.JsonException or System.Text.DecoderFallbackException
            || CliPreflight.IsPathFailure(exception))
        {
            return GitHubProposalPresentation.Local(identity, arguments.Operation, "local-invalid");
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return GitHubProposalPresentation.ContractError(identity, arguments.Operation, null);
        }
    }
}

// Test startup assemblies may register bounded observations/faults by reflection. There is
// no command, configuration, or ordinary environment selector for these process boundaries.
internal static class GitHubProposalProcessHooks
{
    private static Action<string>? observer;
    private static (string Source, long? Revision)? terminal;
    private static void Register(Action<string> callback) => observer = callback;
    private static void RegisterTerminal(string source, long? revision) => terminal = (source, revision);
    internal static CampaignTerminal? Terminal(CampaignOperation operation) => terminal is { } selected
        ? new("campaign", operation, selected.Source, selected.Revision) : null;
    internal static void Reach(string boundary) => observer?.Invoke(boundary);
}
