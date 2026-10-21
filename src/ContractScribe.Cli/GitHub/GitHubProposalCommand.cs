using ContractScribe.Core;
using ContractScribe.GitHub.Transport;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Publication;

namespace ContractScribe.Cli;

internal static class GitHubProposalCommand
{
    internal const string Help =
        "ContractScribe github-proposal\n\n" +
        "Usage:\n" +
        "  contract-scribe github-proposal start --repository-root <path> --input <path> --policy <path> --request <path> [--configuration <path>] [--configuration-override <path>]\n" +
        "  contract-scribe github-proposal resume --repository-root <path> --input <path> --policy <path> --request <path> [--configuration <path>] [--configuration-override <path>]\n\n" +
        "Options:\n" +
        "  --repository-root <path>       Existing repository root.\n" +
        "  --input <path>                 Existing .sln, .slnx, or .csproj inside the repository.\n" +
        "  --policy <path>                Existing M1 policy file inside the repository.\n" +
        "  --request <path>               Strict non-secret github-proposal invocation request JSON.\n" +
        "  --configuration <path>         Optional consumer configuration layer.\n" +
        "  --configuration-override <path>  Optional invocation configuration layer.\n" +
        "  -h, --help                     Print this help.\n\n" +
        "The four required options must appear exactly once; the two configuration layers are optional.\n" +
        "Both --option value and --option=value forms are accepted in any order.\n\n" +
        "Environment:\n" +
        "  CONTRACTSCRIBE_PROVIDER_API_KEY  Existing campaign provider credential.\n" +
        "  CONTRACTSCRIBE_GITHUB_TOKEN      GitHub credential read once after local request/configuration validation.\n\n" +
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
            var request = GitHubProposalRequestReader.Read(arguments.Request, currentDirectory);
            var preflight = CampaignPreflight.Run(arguments.Campaign(request), currentDirectory, identity, remoteCheckpoint: true);
            if (GitHubProposalProcessHooks.Terminal(arguments.Operation) is { } selectedTerminal)
                return GitHubProposalPresentation.Campaign(identity, selectedTerminal);
            var accessor = credentialAccessor ?? Environment.GetEnvironmentVariable;
            if (!request.Revalidate() || !preflight.Configuration.Revalidate())
                return GitHubProposalPresentation.Local(identity, arguments.Operation, "local-invalid");
            var github = request.Request.GitHub;
            var authority = ValidatedGitHubCheckpointAuthority.Create(github.RepositoryOwner, github.RepositoryName,
                github.TargetRef, request.Request.CampaignLineage, github.ExpectedBaseCommitOid);
            GitHubProposalProcessHooks.Reach("token-read");
            GitHubApiClient client;
            try { client = GitHubApiClient.CreateCheckpoint(authority, accessor("CONTRACTSCRIBE_GITHUB_TOKEN")!); }
            catch (Exception exception) when (exception is ArgumentException or GitHubProtocolException)
            { return GitHubProposalPresentation.Local(identity, arguments.Operation, "permission"); }
            using var clientLifetime = client;
            using var finalization = new GitHubPublicationFinalization(cancellationToken, GitHubProposalProcessHooks.FinalizationClock);
            await using var checkpoint = GitHubCoordinationStore.Create(client);
            var observed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, checkpoint, cancellationToken).ConfigureAwait(false);
            if (observed.Failure is not null
                && GitHubCampaignPreflight.Select(identity, arguments.Operation, request.Request, observed, null) is { } lifecycleStop)
                return lifecycleStop;
            var read = await checkpoint.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint.CheckpointFailure is { } readFailure)
                return GitHubProposalPresentation.Local(identity, arguments.Operation, GitHubCampaignPreflight.TransportOutcome(readFailure));
            var accepted = read.Kind == CampaignCheckpointReadKind.Found ? CampaignCheckpointAcceptance.AcceptCurrent(read) : null;
            if (read.Kind is not (CampaignCheckpointReadKind.Found or CampaignCheckpointReadKind.NotFound)
                || read.Kind == CampaignCheckpointReadKind.Found && accepted?.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || read.Kind == CampaignCheckpointReadKind.NotFound && observed.Ledger is not null)
                return GitHubProposalPresentation.Preflight(identity, arguments.Operation, "human-change", "invalid-recovery-authority", null);
            if (GitHubCampaignPreflight.Select(identity, arguments.Operation, request.Request, observed,
                accepted?.Artifact?.CheckpointRevision) is { } verifiedStop) return verifiedStop;
            // Classify lifecycle before a stale caller publication base can mask its reason.
            if (!await client.BindObservedCheckpointBaseAsync(observed, cancellationToken).ConfigureAwait(false))
                return GitHubProposalPresentation.Preflight(identity, arguments.Operation, "conflict", "changed-preflight", accepted?.Artifact?.CheckpointRevision);
            var claim = await checkpoint.AcquireCheckpointAsync(cancellationToken).ConfigureAwait(false);
            if (claim != CampaignCheckpointReadKind.Found)
            {
                var outcome = checkpoint.CheckpointFailure?.Code switch
                {
                    GitHubFailureCode.Authentication or GitHubFailureCode.Permission => "permission",
                    GitHubFailureCode.RateLimit => "rate-limit",
                    GitHubFailureCode.Cancelled => "cancelled",
                    GitHubFailureCode.Timeout => "timeout",
                    _ => claim is CampaignCheckpointReadKind.LeaseConflict or CampaignCheckpointReadKind.LeaseUnverifiable
                        ? "conflict" : claim == CampaignCheckpointReadKind.Invalid ? "human-change"
                        : claim == CampaignCheckpointReadKind.Unreadable ? "host-failure" : "local-invalid",
                };
                return GitHubProposalPresentation.Local(identity, arguments.Operation, outcome);
            }
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, checkpoint.CheckpointToken);
            var claimed = await GitHubCampaignObserver.ObserveCheckpointAsync(client, checkpoint, execution.Token).ConfigureAwait(false);
            if (GitHubCampaignPreflight.Select(identity, arguments.Operation, request.Request, claimed,
                accepted?.Artifact?.CheckpointRevision) is { } claimedStop) return claimedStop;
            if (GitHubCampaignPreflight.NeedsPublicationRecovery(claimed))
            {
                var recoveryToken = finalization.Begin(execution.Token);
                client.BindFinalizationDeadline(finalization.DeadlineToken);
                claimed = await checkpoint.RecoverObservedPublicationAsync(recoveryToken).ConfigureAwait(false);
                GitHubProposalProcessHooks.Reach("after-publication-recovery");
                if (finalization.TimedOut && claimed.Failure?.Transport?.Code == GitHubFailureCode.Cancelled)
                    return GitHubProposalPresentation.Local(identity, arguments.Operation, "timeout", accepted?.Artifact?.CheckpointRevision);
                if (GitHubCampaignPreflight.Select(identity, arguments.Operation, request.Request, claimed,
                    accepted?.Artifact?.CheckpointRevision) is { } recoveredStop) return recoveredStop;
                if (finalization.TimedOut) return GitHubProposalPresentation.Local(identity, arguments.Operation, "timeout", accepted?.Artifact?.CheckpointRevision);
            }
            if (claimed.TargetOid != observed.TargetOid)
                return GitHubProposalPresentation.Preflight(identity, arguments.Operation, "conflict", "changed-preflight", accepted?.Artifact?.CheckpointRevision);
            if (!request.Revalidate() || !preflight.Configuration.Revalidate())
                return GitHubProposalPresentation.Local(identity, arguments.Operation, "local-invalid", accepted?.Artifact?.CheckpointRevision);
            preflight = preflight with { SnapshotBinding = GitHubCampaignPreflight.SnapshotBinding(preflight, request.Request, claimed) };
            var originalOnly = claimed.Ledger?.Stage is GitHubCoordinationStage.Claimed
                or GitHubCoordinationStage.ContentCreated or GitHubCoordinationStage.ProposalRefAdvanced;
            if (originalOnly && accepted?.Artifact is { } frozen
                && preflight.SnapshotBinding != frozen.State.Snapshot.OpaqueSnapshotBinding)
                return GitHubProposalPresentation.Preflight(identity, arguments.Operation, "conflict", "publication-incomplete", frozen.CheckpointRevision);
            var continuation = new CampaignAcceptedCandidateContinuation(identity, preflight, request, accessor,
                (publication, payload, token) => GitHubPublicationFacade.PublishCheckpointAsync(client, checkpoint,
                    publication, payload, token),
                async (bundle, token) =>
                {
                    try
                    {
                        if (!request.Revalidate() || !preflight.Configuration.Revalidate())
                            return GitHubProposalPresentation.Local(identity, arguments.Operation, "local-invalid", accepted?.Artifact?.CheckpointRevision);
                        var inputFailure = await GitHubCheckoutBinding.ValidateAsync(client, claimed.TargetOid!, preflight, bundle, token).ConfigureAwait(false);
                        return inputFailure is null ? null : GitHubProposalPresentation.Preflight(identity, arguments.Operation,
                            "stale", inputFailure, accepted?.Artifact?.CheckpointRevision);
                    }
                    catch (GitHubCoordinationException exception) when (exception.TransportFailure is not null)
                    {
                        return GitHubProposalPresentation.Local(identity, arguments.Operation,
                            GitHubCampaignPreflight.TransportOutcome(exception.TransportFailure), accepted?.Artifact?.CheckpointRevision);
                    }
                },
                authenticatedBaseOid: claimed.TargetOid, finalization: finalization,
                bindFinalizationDeadline: client.BindFinalizationDeadline, originalOperationOnly: originalOnly);
            return await CampaignCommandRunner.RunAsync(identity, preflight, execution.Token,
                accessor, continuation, checkpointStore: checkpoint).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GitHubProposalPresentation.Local(identity, arguments.Operation, "cancelled");
        }
        catch (GitHubCoordinationException exception) when (exception.TransportFailure is not null)
        {
            return GitHubProposalPresentation.Local(identity, arguments.Operation,
                GitHubCampaignPreflight.TransportOutcome(exception.TransportFailure));
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
    private static TimeProvider? finalizationClock;
    private static void Register(Action<string> callback) => observer = callback;
    private static void RegisterTerminal(string source, long? revision) => terminal = (source, revision);
    private static void RegisterFinalizationClock(TimeProvider clock) => finalizationClock = clock;
    internal static TimeProvider? FinalizationClock => finalizationClock;
    internal static CampaignTerminal? Terminal(CampaignOperation operation) => terminal is { } selected
        ? new("campaign", operation, selected.Source, selected.Revision) : null;
    internal static void Reach(string boundary) => observer?.Invoke(boundary);
}

// A retained handler must keep its cancellation source alive through physical output.
internal sealed class GitHubProposalSignalLifetime : IDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly AuditSignalRegistration? signals;
    private readonly CancellationTokenRegistration observation;

    internal GitHubProposalSignalLifetime(CancellationToken caller, bool install)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        try
        {
            observation = stop.Token.Register(static () => GitHubProposalProcessHooks.Reach("signal-observed"));
            signals = install ? AuditSignalRegistration.Install(stop) : null;
        }
        catch
        {
            observation.Dispose();
            stop.Dispose();
            throw;
        }
    }

    internal CancellationToken Token => stop.Token;

    public void Dispose()
    {
        try { signals?.Dispose(); }
        finally
        {
            try { observation.Dispose(); }
            finally { stop.Dispose(); }
        }
    }
}
