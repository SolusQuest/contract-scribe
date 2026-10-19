using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum DocumentationScribeAcknowledgementStage
{
    Received, Validated, DurablyRecorded, PatchAccepted,
}

public enum DocumentationScribeFinishReason
{
    Completed, Unresolved, Context,
}

public sealed record DocumentationScribeFinishedTerminal : DocumentationScribeTerminal
{
    internal DocumentationScribeFinishedTerminal(DocumentationScribeFinishReason reason)
        : base(DocumentationScribeTerminalKind.Finished) => Reason = reason;
    public DocumentationScribeFinishReason Reason { get; }
}

/// <summary>Core-validated documentation; contains no source bodies or private conversation.</summary>
public sealed class DocumentationScribeValidatedSubmission
{
    internal DocumentationScribeValidatedSubmission(string requestSha256, string targetId,
        string contentSha256, DocumentationScribeTerminal result,
        ImmutableArray<DocumentationScribeEvidenceReference> dynamicEvidenceReferences,
        ImmutableArray<byte> resultUtf8Json)
    {
        ScribeRequestSha256 = requestSha256; TargetId = targetId; ContentSha256 = contentSha256;
        Result = result; DynamicEvidenceReferences = dynamicEvidenceReferences; ResultUtf8Json = resultUtf8Json;
    }
    public string ScribeRequestSha256 { get; }
    public string TargetId { get; }
    public string ContentSha256 { get; }
    public DocumentationScribeTerminal Result { get; }
    public ImmutableArray<DocumentationScribeEvidenceReference> DynamicEvidenceReferences { get; }
    public ImmutableArray<byte> ResultUtf8Json { get; }
    public override string ToString() => nameof(DocumentationScribeValidatedSubmission);
}

/// <summary>A Host assertion, never a provider assertion or authority to publish.</summary>
public sealed record DocumentationScribeSubmissionAcknowledgement(
    string ScribeRequestSha256, string TargetId, string ContentSha256,
    DocumentationScribeAcknowledgementStage Stage)
{
    public override string ToString() => nameof(DocumentationScribeSubmissionAcknowledgement);
}

/// <summary>The Host owns persistence and must confirm readback before acknowledging durability.</summary>
/// <remarks>An interrupted call may have effects. The runtime never retries an uncertain callback.</remarks>
public interface IDocumentationScribeSubmissionHost
{
    ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
        DocumentationScribeValidatedSubmission submission, CancellationToken cancellationToken);

    /// <summary>Confirms completed batch, rejection or finish progress before another model request.</summary>
    /// <remarks>The original Host permit and its deadline also bound this call. Uncertain effects require readback.</remarks>
    ValueTask<DocumentationScribeProgressAcknowledgement?> ObserveProgressAsync(
        DocumentationScribeProgressNotification progress, CancellationToken cancellationToken);
}

public sealed class DocumentationScribeTargetSubmission
{
    internal DocumentationScribeTargetSubmission(DocumentationScribeValidatedSubmission validation,
        DocumentationScribeAcknowledgementStage stage)
    { Validation = validation; Stage = stage; }
    public DocumentationScribeValidatedSubmission Validation { get; }
    public string TargetId => Validation.TargetId;
    public string ContentSha256 => Validation.ContentSha256;
    public DocumentationScribeTerminal Result => Validation.Result;
    public DocumentationScribeAcknowledgementStage Stage { get; }
    public override string ToString() => nameof(DocumentationScribeTargetSubmission);
}

public sealed record DocumentationScribeTargetFailure(string TargetId, string ValidationCode)
{
    public override string ToString() => nameof(DocumentationScribeTargetFailure);
}

public sealed record DocumentationScribeCoverage(string TargetId, AuditOutcome AuditOutcome,
    string Status, DocumentationScribeAcknowledgementStage? Stage)
{
    public override string ToString() => nameof(DocumentationScribeCoverage);
}

public sealed record DocumentationScribeSubmissionParseResult(
    DocumentationScribeValidatedSubmission? Submission, DocumentationScribeResultValidationFailure? Failure);

/// <summary>Physical requests reporting each field; aggregate values remain nullable observed sums.</summary>
/// <remarks>A complete total requires both a represented value and reports from every physical request.
/// Missing coverage or a missing aggregate never means zero. Conservative charges are not measurements.</remarks>
public sealed record DocumentationScribeUsageCoverage(int InputRequests, int OutputRequests,
    int CachedInputRequests, int UncachedInputRequests, int ReasoningRequests, int CostRequests, int CacheRequests);
