using System.Collections.Immutable;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

public sealed class DocumentationScribeContextContent
{
    public DocumentationScribeContextContent(
        string contextReferenceId,
        DocumentationScribeContextReferenceKind kind,
        string contentSha256,
        int includedUtf8ByteCount,
        bool isTruncated,
        string content)
    {
        ContextReferenceId = DocumentationScribeBoundary.ValidateIdentifier(
            contextReferenceId,
            nameof(contextReferenceId));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ContentSha256 = DocumentationScribeBoundary.ValidateSha256(contentSha256, nameof(contentSha256));
        Content = DocumentationScribeBoundary.ValidateCommittedText(
            content,
            nameof(content),
            DocumentationScribeBoundary.MaximumPromptBlockUtf8Bytes);
        var actualBytes = Encoding.UTF8.GetByteCount(Content);
        if (includedUtf8ByteCount < 0 || includedUtf8ByteCount != actualBytes)
        {
            throw new ArgumentException(
                "The content does not match its included-byte commitment.",
                nameof(includedUtf8ByteCount));
        }

        if (!isTruncated
            && !DocumentationScribeBoundary.MatchesContentSha256(Content, ContentSha256))
        {
            throw new ArgumentException(
                "The content does not match its SHA-256 commitment.",
                nameof(contentSha256));
        }

        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
    }

    public string ContextReferenceId { get; }

    public DocumentationScribeContextReferenceKind Kind { get; }

    public string ContentSha256 { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public string Content { get; }

    public override string ToString() => nameof(DocumentationScribeContextContent);
}

public sealed class DocumentationScribeEvidenceContent
{
    public DocumentationScribeEvidenceContent(
        string evidenceReferenceId,
        DocumentationScribeEvidenceAuthority authority,
        string contentSha256,
        int includedUtf8ByteCount,
        bool isTruncated,
        string content)
    {
        EvidenceReferenceId = DocumentationScribeBoundary.ValidateIdentifier(
            evidenceReferenceId,
            nameof(evidenceReferenceId));
        if (!Enum.IsDefined(authority))
        {
            throw new ArgumentOutOfRangeException(nameof(authority));
        }

        Authority = authority;
        ContentSha256 = DocumentationScribeBoundary.ValidateSha256(contentSha256, nameof(contentSha256));
        Content = DocumentationScribeBoundary.ValidateCommittedText(
            content,
            nameof(content),
            DocumentationScribeBoundary.MaximumPromptBlockUtf8Bytes);
        var actualBytes = Encoding.UTF8.GetByteCount(Content);
        if (includedUtf8ByteCount < 0 || includedUtf8ByteCount != actualBytes)
        {
            throw new ArgumentException(
                "The content does not match its included-byte commitment.",
                nameof(includedUtf8ByteCount));
        }

        if (!isTruncated
            && !DocumentationScribeBoundary.MatchesContentSha256(Content, ContentSha256))
        {
            throw new ArgumentException(
                "The content does not match its SHA-256 commitment.",
                nameof(contentSha256));
        }

        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
    }

    public string EvidenceReferenceId { get; }

    public DocumentationScribeEvidenceAuthority Authority { get; }

    public string ContentSha256 { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public string Content { get; }

    public override string ToString() => nameof(DocumentationScribeEvidenceContent);
}

public sealed class DocumentationScribePromptInput
{
    public DocumentationScribePromptInput(
        ImmutableArray<DocumentationScribeContextContent> context,
        ImmutableArray<DocumentationScribeEvidenceContent> evidence)
    {
        if (context.IsDefault || context.Length > DocumentationScribeContract.MaximumReferences)
        {
            throw new ArgumentException("The context collection is not bounded.", nameof(context));
        }

        if (evidence.IsDefault || evidence.Length > DocumentationScribeContract.MaximumReferences)
        {
            throw new ArgumentException("The evidence collection is not bounded.", nameof(evidence));
        }

        if (context.Any(item => item is null) || evidence.Any(item => item is null))
        {
            throw new ArgumentException("Prompt content cannot contain null entries.");
        }

        long totalUtf8Bytes;
        try
        {
            totalUtf8Bytes = context.Aggregate(
                0L,
                (total, item) => checked(total + item.IncludedUtf8ByteCount));
            totalUtf8Bytes = evidence.Aggregate(
                totalUtf8Bytes,
                (total, item) => checked(total + item.IncludedUtf8ByteCount));
        }
        catch (OverflowException)
        {
            throw new ArgumentException("The prompt content is outside the product boundary.");
        }

        if (totalUtf8Bytes > DocumentationScribeBoundary.MaximumLogicalRequestUtf8Bytes)
        {
            throw new ArgumentException("The prompt content is outside the product boundary.");
        }

        Context = context;
        Evidence = evidence;
    }

    public ImmutableArray<DocumentationScribeContextContent> Context { get; }

    public ImmutableArray<DocumentationScribeEvidenceContent> Evidence { get; }

    public override string ToString() => nameof(DocumentationScribePromptInput);
}
