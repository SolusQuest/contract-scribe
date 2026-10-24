using ContractScribe.GitHub.Transport;
using System.Security.Cryptography;
using System.Text;

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
internal enum GitHubCheckpointRefRelation { Expected, Predecessor, Other, Unavailable }

// Only this transaction's strict decoded final GET and actual mutation result.
// updateRefs returns a matched clientMutationId acknowledgement, never an OID.
internal sealed record GitHubCheckpointRefComparison(string ExpectedCommit, string? PredecessorCommit,
    string? ObservedCommit, GitHubCheckpointRefRelation Relation, GitHubDelivery Delivery, bool Acknowledged) : GitHubValue
{
    internal bool IsValid => Hash(ExpectedCommit) && (PredecessorCommit is null || Hash(PredecessorCommit))
        && (ObservedCommit is null || Hash(ObservedCommit)) && Enum.IsDefined(Relation)
        && Delivery is GitHubDelivery.NeedsReadback or GitHubDelivery.Ambiguous
        && Acknowledged == (Delivery == GitHubDelivery.NeedsReadback)
        && (Relation switch
        {
            GitHubCheckpointRefRelation.Unavailable => ObservedCommit is null,
            GitHubCheckpointRefRelation.Expected => ObservedCommit == ExpectedCommit,
            GitHubCheckpointRefRelation.Predecessor => ObservedCommit is not null && ObservedCommit == PredecessorCommit
                && ObservedCommit != ExpectedCommit,
            GitHubCheckpointRefRelation.Other => ObservedCommit is not null && ObservedCommit != ExpectedCommit
                && ObservedCommit != PredecessorCommit,
            _ => false,
        });

    internal static GitHubCheckpointRefComparison Create(string expected, string predecessor, string? observed,
        GitHubApiResult<GitHubAcknowledgement> mutation) => new(Fingerprint(expected),
        predecessor == new string('0', 40) ? null : Fingerprint(predecessor), observed is null ? null : Fingerprint(observed),
        observed is null ? GitHubCheckpointRefRelation.Unavailable
            : observed == expected ? GitHubCheckpointRefRelation.Expected
            : observed == predecessor ? GitHubCheckpointRefRelation.Predecessor : GitHubCheckpointRefRelation.Other,
        mutation.Delivery, mutation.Value is not null && mutation.Failure is null);

    private static string Fingerprint(string oid) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.ASCII.GetBytes("contract-scribe.checkpoint.commit\0" + oid)));
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// A single command-local failed operation, not an acknowledgement or capability.
internal sealed record GitHubCheckpointDiagnostic(long OperationOrdinal, GitHubCheckpointOperation Operation,
    long? AttemptedRevision, GitHubCheckpointStage Stage, GitHubCheckpointReason Reason,
    GitHubFailureCode? TransportCode = null, int? HttpStatus = null,
    GitHubCheckpointCancellation? Cancellation = null, GitHubCheckpointRefComparison? RefComparison = null) : GitHubValue
{
    internal bool IsValid => OperationOrdinal > 0 && AttemptedRevision is null or >= 0
        && Enum.IsDefined(Operation) && Enum.IsDefined(Stage) && Enum.IsDefined(Reason)
        && (TransportCode is null || Enum.IsDefined(TransportCode.Value))
        && HttpStatus is null or >= 100 and <= 599
        && (HttpStatus is null || TransportCode is not null)
        && (Cancellation is null || Enum.IsDefined(Cancellation.Value))
        && (Reason == GitHubCheckpointReason.Cancelled) == (Cancellation is not null)
        && (RefComparison is null || Stage == GitHubCheckpointStage.FinalRef && RefComparison.IsValid);
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
        GitHubCheckpointReason reason, GitHubFailure? failure = null, GitHubCheckpointCancellation? cancellation = null,
        GitHubCheckpointRefComparison? refComparison = null)
    {
        if (attempt is null) return; // Cleanup never creates a command failure observation.
        var observation = new GitHubCheckpointDiagnostic(attempt.Ordinal, attempt.Operation, attempt.Revision,
            stage, reason, failure?.Code, failure?.HttpStatus, cancellation, refComparison);
        if (observation.IsValid) Interlocked.CompareExchange(ref checkpointDiagnostic, observation, null);
    }

    private void ObserveCheckpointException(CheckpointAttempt? attempt, GitHubCheckpointStage stage,
        Exception exception, CancellationToken token, bool recovery = false, GitHubCheckpointRefComparison? refComparison = null)
    {
        var failure = (exception as GitHubCoordinationException)?.TransportFailure
            ?? (exception.InnerException as GitHubCoordinationException)?.TransportFailure;
        var cancelled = exception is OperationCanceledException || failure?.Code == GitHubFailureCode.Cancelled
            || failure?.Code == GitHubFailureCode.Timeout && token.IsCancellationRequested;
        ObserveCheckpointFailure(attempt, stage, cancelled ? GitHubCheckpointReason.Cancelled
            : failure is not null ? GitHubCheckpointReason.Transport
            : exception is GitHubCoordinationException or ContractScribe.Core.CampaignStateValidationException
                ? GitHubCheckpointReason.InvalidData : GitHubCheckpointReason.Unknown,
            failure, cancelled ? token.IsCancellationRequested
                ? recovery ? GitHubCheckpointCancellation.RecoveryToken : GitHubCheckpointCancellation.OperationToken
                : GitHubCheckpointCancellation.Unknown : null, refComparison);
    }
}
