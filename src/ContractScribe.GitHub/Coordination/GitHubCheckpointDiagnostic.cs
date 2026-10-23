using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal enum GitHubCheckpointOperation { Acquire, Read, Write, Renew, Publication }
internal enum GitHubCheckpointStage
{
    Gate, PredecessorRead, CreateObjects, RefMutation, FinalRef, CanonicalDocument,
    CheckpointReadback, OwnerConfirmation, LeaseConfirmation,
}
internal enum GitHubCheckpointReason
{
    Transport, InvalidData, RefMismatch, DocumentMismatch, CheckpointMismatch,
    OwnerMismatch, LeaseUnverifiable, Cancelled, Unknown,
}
internal enum GitHubCheckpointCancellation { OperationToken, RecoveryToken, Unknown }

// A single command-local failed operation, not an acknowledgement or capability.
internal sealed record GitHubCheckpointDiagnostic(long OperationOrdinal, GitHubCheckpointOperation Operation,
    long? AttemptedRevision, GitHubCheckpointStage Stage, GitHubCheckpointReason Reason,
    GitHubFailureCode? TransportCode = null, int? HttpStatus = null,
    GitHubCheckpointCancellation? Cancellation = null) : GitHubValue
{
    internal bool IsValid => OperationOrdinal > 0 && AttemptedRevision is null or >= 0
        && Enum.IsDefined(Operation) && Enum.IsDefined(Stage) && Enum.IsDefined(Reason)
        && (TransportCode is null || Enum.IsDefined(TransportCode.Value))
        && HttpStatus is null or >= 100 and <= 599
        && (HttpStatus is null || TransportCode is not null)
        && (Cancellation is null || Enum.IsDefined(Cancellation.Value))
        && (Reason == GitHubCheckpointReason.Cancelled) == (Cancellation is not null);
}

internal sealed partial class GitHubCoordinationStore
{
    private long checkpointOperationOrdinal;
    private GitHubCheckpointDiagnostic? checkpointDiagnostic;
    internal GitHubCheckpointDiagnostic? CheckpointDiagnostic => Volatile.Read(ref checkpointDiagnostic);
    private sealed record CheckpointAttempt(long Ordinal, GitHubCheckpointOperation Operation, long? Revision);
    private CheckpointAttempt BeginCheckpointOperation(GitHubCheckpointOperation operation, long? revision = null) =>
        new(Interlocked.Increment(ref checkpointOperationOrdinal), operation, revision);

    private void ObserveCheckpointFailure(CheckpointAttempt? attempt, GitHubCheckpointStage stage,
        GitHubCheckpointReason reason, GitHubFailure? failure = null, GitHubCheckpointCancellation? cancellation = null)
    {
        if (attempt is null) return; // Cleanup never creates a command failure observation.
        var observation = new GitHubCheckpointDiagnostic(attempt.Ordinal, attempt.Operation, attempt.Revision,
            stage, reason, failure?.Code, failure?.HttpStatus, cancellation);
        if (observation.IsValid) Interlocked.CompareExchange(ref checkpointDiagnostic, observation, null);
    }

    private void ObserveCheckpointException(CheckpointAttempt? attempt, GitHubCheckpointStage stage,
        Exception exception, CancellationToken token, bool recovery = false)
    {
        var failure = (exception as GitHubCoordinationException)?.TransportFailure;
        var cancelled = exception is OperationCanceledException || failure?.Code == GitHubFailureCode.Cancelled;
        ObserveCheckpointFailure(attempt, stage, cancelled ? GitHubCheckpointReason.Cancelled
            : failure is not null ? GitHubCheckpointReason.Transport
            : exception is GitHubCoordinationException or ContractScribe.Core.CampaignStateValidationException
                ? GitHubCheckpointReason.InvalidData : GitHubCheckpointReason.Unknown,
            failure, cancelled ? token.IsCancellationRequested
                ? recovery ? GitHubCheckpointCancellation.RecoveryToken : GitHubCheckpointCancellation.OperationToken
                : GitHubCheckpointCancellation.Unknown : null);
    }
}
