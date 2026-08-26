using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ContractScribe.Core;

public enum CampaignCheckpointReadKind
{
    NotFound,
    Found,
    Invalid,
    Unreadable,
}

public sealed class CampaignCheckpointReadResult
{
    private CampaignCheckpointReadResult(
        CampaignCheckpointReadKind kind,
        ImmutableArray<byte> exactUtf8Json,
        long? checkpointRevision,
        string? sha256)
    {
        Kind = kind;
        ExactUtf8Json = exactUtf8Json;
        CheckpointRevision = checkpointRevision;
        Sha256 = sha256;
    }

    public CampaignCheckpointReadKind Kind { get; }
    public ImmutableArray<byte> ExactUtf8Json { get; }
    public long? CheckpointRevision { get; }
    public string? Sha256 { get; }

    public static CampaignCheckpointReadResult NotFound() =>
        new(CampaignCheckpointReadKind.NotFound, default, null, null);

    public static CampaignCheckpointReadResult Found(
        ReadOnlySpan<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256) =>
        new(
            CampaignCheckpointReadKind.Found,
            ImmutableArray.CreateRange(exactUtf8Json.ToArray()),
            checkpointRevision,
            sha256);

    public static CampaignCheckpointReadResult Invalid() =>
        new(CampaignCheckpointReadKind.Invalid, default, null, null);

    public static CampaignCheckpointReadResult Unreadable() =>
        new(CampaignCheckpointReadKind.Unreadable, default, null, null);
}

public enum CampaignCheckpointWriteKind
{
    Written,
    AlreadyPresent,
    PredecessorMissing,
    CurrentMismatch,
    Unwritable,
}

public readonly record struct CampaignCheckpointWriteResult(CampaignCheckpointWriteKind Kind);

public interface ICampaignCheckpointStore
{
    ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken);

    ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken);

    ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
        long expectedCheckpointRevision,
        string expectedSha256,
        ReadOnlyMemory<byte> exactUtf8Json,
        long checkpointRevision,
        string sha256,
        CancellationToken cancellationToken);
}

public enum CampaignCheckpointAcceptanceKind
{
    Accepted,
    Conflict,
    InvalidRead,
    Unreadable,
    WriteRejected,
    ReadbackMismatch,
    InvalidTransition,
    Cancelled,
}

public sealed record CampaignCheckpointAcceptanceResult(
    CampaignCheckpointAcceptanceKind Kind,
    CampaignCheckpointArtifact? Artifact);

/// <summary>
/// Applies a pure reducer result through a conditional store and accepts it
/// only after exact canonical readback.
/// </summary>
public static class CampaignCheckpointAcceptance
{
    public static async ValueTask<CampaignCheckpointAcceptanceResult> AcceptAsync(
        ICampaignCheckpointStore store,
        CampaignCheckpointArtifact? expectedPredecessor,
        CampaignTransitionResult transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            return new CampaignCheckpointAcceptanceResult(
                CampaignCheckpointAcceptanceKind.InvalidTransition,
                null);
        }

        try
        {
            var current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var currentMatch = Match(current, transition.Artifact);
            if (currentMatch == MatchKind.Invalid)
            {
                return ReadFailure(current);
            }

            if (transition.Kind == CampaignTransitionKind.Unchanged || currentMatch == MatchKind.Exact)
            {
                return currentMatch == MatchKind.Exact
                    ? await VerifyReadbackAsync(store, transition.Artifact, cancellationToken).ConfigureAwait(false)
                    : new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Conflict, null);
            }

            CampaignCheckpointWriteResult write;
            if (expectedPredecessor is null)
            {
                if (current.Kind != CampaignCheckpointReadKind.NotFound)
                {
                    return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Conflict, null);
                }

                write = await store.CreateIfAbsentAsync(
                    transition.Artifact.ExactUtf8Json.AsMemory(),
                    transition.Artifact.CheckpointRevision,
                    transition.Artifact.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (Match(current, expectedPredecessor) != MatchKind.Exact)
                {
                    return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Conflict, null);
                }

                write = await store.ReplaceIfCurrentAsync(
                    expectedPredecessor.CheckpointRevision,
                    expectedPredecessor.Sha256,
                    transition.Artifact.ExactUtf8Json.AsMemory(),
                    transition.Artifact.CheckpointRevision,
                    transition.Artifact.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }

            if (write.Kind != CampaignCheckpointWriteKind.Written)
            {
                return new CampaignCheckpointAcceptanceResult(
                    write.Kind is CampaignCheckpointWriteKind.AlreadyPresent
                        or CampaignCheckpointWriteKind.PredecessorMissing
                        or CampaignCheckpointWriteKind.CurrentMismatch
                        ? CampaignCheckpointAcceptanceKind.Conflict
                        : CampaignCheckpointAcceptanceKind.WriteRejected,
                    null);
            }

            return await VerifyReadbackAsync(store, transition.Artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Cancelled, null);
        }
    }

    private static async ValueTask<CampaignCheckpointAcceptanceResult> VerifyReadbackAsync(
        ICampaignCheckpointStore store,
        CampaignCheckpointArtifact intended,
        CancellationToken cancellationToken)
    {
        var readback = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Match(readback, intended) == MatchKind.Exact
            ? new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.Accepted, intended)
            : new CampaignCheckpointAcceptanceResult(CampaignCheckpointAcceptanceKind.ReadbackMismatch, null);
    }

    private static CampaignCheckpointAcceptanceResult ReadFailure(CampaignCheckpointReadResult read) =>
        new(
            read.Kind == CampaignCheckpointReadKind.Unreadable
                ? CampaignCheckpointAcceptanceKind.Unreadable
                : CampaignCheckpointAcceptanceKind.InvalidRead,
            null);

    private static MatchKind Match(
        CampaignCheckpointReadResult read,
        CampaignCheckpointArtifact artifact)
    {
        if (read.Kind == CampaignCheckpointReadKind.NotFound)
        {
            return MatchKind.Different;
        }

        if (read.Kind != CampaignCheckpointReadKind.Found
            || read.CheckpointRevision is null
            || read.Sha256 is null
            || read.ExactUtf8Json.IsDefault)
        {
            return MatchKind.Invalid;
        }

        var bytes = read.ExactUtf8Json.AsSpan();
        var recomputed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var parsed = CampaignStateJson.Parse(read.ExactUtf8Json.AsMemory());
        if (!string.Equals(recomputed, read.Sha256, StringComparison.Ordinal)
            || !parsed.IsValid
            || parsed.Artifact is null
            || parsed.Artifact.CheckpointRevision != read.CheckpointRevision)
        {
            return MatchKind.Invalid;
        }

        return read.CheckpointRevision == artifact.CheckpointRevision
            && string.Equals(read.Sha256, artifact.Sha256, StringComparison.Ordinal)
            && bytes.SequenceEqual(artifact.ExactUtf8Json.AsSpan())
            && parsed.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(artifact.ExactUtf8Json.AsSpan())
            ? MatchKind.Exact
            : MatchKind.Different;
    }

    private enum MatchKind
    {
        Exact,
        Different,
        Invalid,
    }
}
