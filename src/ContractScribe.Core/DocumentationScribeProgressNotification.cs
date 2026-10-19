using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ContractScribe.Core;

public sealed record DocumentationScribeProgressReceipt(string TargetId, string ContentSha256,
    DocumentationScribeAcknowledgementStage Stage);

/// <summary>Bounded scalar progress; contains no provider messages, source or store capability.</summary>
public sealed class DocumentationScribeProgressNotification
{
    internal DocumentationScribeProgressNotification(string requestSha256, DocumentationScribeAttemptId attemptId,
        int repairCount, int nextSequence, ImmutableArray<string> attemptedTargetIds,
        ImmutableArray<DocumentationScribeProgressReceipt> receipts,
        ImmutableArray<DocumentationScribeTargetFailure> failures, bool stopped, bool finished)
    {
        ScribeRequestSha256 = requestSha256; AttemptId = attemptId; RepairCount = repairCount;
        NextSequence = nextSequence; AttemptedTargetIds = attemptedTargetIds; Receipts = receipts;
        Failures = failures; Stopped = stopped; Finished = finished;
        ProgressSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "contract-scribe/scribe-progress/v1",
            requestSha256,
            attemptId = attemptId.Value,
            repairCount,
            nextSequence,
            attemptedTargetIds,
            receipts,
            failures,
            stopped,
            finished,
        }))).ToLowerInvariant();
    }
    public string ScribeRequestSha256 { get; }
    public DocumentationScribeAttemptId AttemptId { get; }
    public int RepairCount { get; }
    public int NextSequence { get; }
    public ImmutableArray<string> AttemptedTargetIds { get; }
    public ImmutableArray<DocumentationScribeProgressReceipt> Receipts { get; }
    public ImmutableArray<DocumentationScribeTargetFailure> Failures { get; }
    public bool Stopped { get; }
    public bool Finished { get; }
    public string ProgressSha256 { get; }
    public override string ToString() => nameof(DocumentationScribeProgressNotification);
}

/// <summary>Confirms exactly one progress snapshot, independently of member durability receipts.</summary>
public sealed record DocumentationScribeProgressAcknowledgement(string ProgressSha256);

public static partial class DocumentationScribeValidation
{
    public static DocumentationScribeProgressNotification CreateProgressNotification(DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId, int repairCount, int nextSequence,
        IEnumerable<string> attemptedTargetIds, IEnumerable<DocumentationScribeTargetSubmission> submissions,
        IEnumerable<DocumentationScribeTargetFailure> failures, bool stopped, bool finished = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempted = attemptedTargetIds.Take(DocumentationScribeContract.MaximumManifestTargets + 1)
            .Order(StringComparer.Ordinal).ToImmutableArray();
        var accepted = submissions.Take(DocumentationScribeContract.MaximumManifestTargets + 1)
            .OrderBy(item => item.TargetId, StringComparer.Ordinal).ToImmutableArray();
        var rejected = failures.Take(DocumentationScribeContract.MaximumManifestTargets + 1)
            .OrderBy(item => item.TargetId, StringComparer.Ordinal).ToImmutableArray();
        if (finished && !stopped || !DocumentationScribeAttemptId.TryParse(attemptId.Value, out _) || repairCount is < 0 or > MaximumInvalidSubmissions
            || nextSequence < 1 || attempted.Length > request.ExecutableTargetIds.Length
            || accepted.Length > request.ExecutableTargetIds.Length || rejected.Length > request.ExecutableTargetIds.Length
            || attempted.Distinct(StringComparer.Ordinal).Count() != attempted.Length
            || accepted.Select(item => item.TargetId).Distinct(StringComparer.Ordinal).Count() != accepted.Length
            || rejected.Select(item => item.TargetId).Distinct(StringComparer.Ordinal).Count() != rejected.Length
            || attempted.Any(id => !request.ExecutableTargetIds.Contains(id, StringComparer.Ordinal))
            || accepted.Any(item => item.Validation.ScribeRequestSha256 != request.ArtifactSha256
                || !Enum.IsDefined(item.Stage) || !request.ExecutableTargetIds.Contains(item.TargetId, StringComparer.Ordinal))
            || rejected.Any(item => !request.ExecutableTargetIds.Contains(item.TargetId, StringComparer.Ordinal)
                || string.IsNullOrEmpty(item.ValidationCode) || item.ValidationCode.Length > 128
                || item.ValidationCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            || accepted.Any(item => rejected.Any(failure => failure.TargetId == item.TargetId)))
            throw new ArgumentException("Progress is not correlated to the current manifest.");
        return new(request.ArtifactSha256, attemptId, repairCount, nextSequence, attempted,
            accepted.Select(item => new DocumentationScribeProgressReceipt(item.TargetId, item.ContentSha256, item.Stage)).ToImmutableArray(),
            rejected, stopped, finished);
    }
}
