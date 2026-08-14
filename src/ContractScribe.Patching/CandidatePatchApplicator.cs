using System.Collections.Immutable;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Patching.Resolution;
using ContractScribe.Roslyn;

namespace ContractScribe.Patching;

public enum DocumentationPatchApplicationStatus
{
    Complete,
    Stale,
    Rejected,
    Failure,
}

public sealed record DocumentationPatchApplicationResult
{
    internal DocumentationPatchApplicationResult(
        DocumentationPatchApplicationStatus status,
        string? primaryCode,
        string? primaryBlockId,
        DocumentationPatchCandidateHandle? candidate)
    {
        Status = status;
        PrimaryCode = primaryCode;
        PrimaryBlockId = primaryBlockId;
        Candidate = candidate;
    }

    public DocumentationPatchApplicationStatus Status { get; }

    public string? PrimaryCode { get; }

    public string? PrimaryBlockId { get; }

    public DocumentationPatchCandidateHandle? Candidate { get; }
}

internal enum DocumentationPatchApplicationStage
{
    BaselineCaptured,
    ResolutionCompleted,
    RenderingCompleted,
    CandidateRootCreated,
    CandidateEntryWritten,
    CandidateReadbackComplete,
    BeforeOriginalRebind,
    AfterSealBeforeReturn,
}

public sealed class CandidatePatchApplicator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);
    private readonly DocumentationPatchResolver resolver;
    private readonly Action<DocumentationPatchApplicationStage, string?>? observer;

    public CandidatePatchApplicator()
        : this(new DocumentationPatchResolver(), null)
    {
    }

    internal CandidatePatchApplicator(
        DocumentationPatchResolver resolver,
        Action<DocumentationPatchApplicationStage, string?>? observer)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.observer = observer;
    }

    public DocumentationPatchApplicationResult Apply(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentationPatchCandidateHandle? candidate = null;
        try
        {
            var capture = session.RepositorySession.CaptureDocumentationPatchRepositoryBaseline(
                cancellationToken);
            if (capture.Baseline is not { } baseline)
            {
                return Failed(
                    capture.Status == DocumentationPatchRepositoryBaselineStatus.Rejected
                        ? DocumentationPatchApplicationStatus.Rejected
                        : DocumentationPatchApplicationStatus.Stale,
                    capture.FailureCode ?? "patch.stale.repository-context");
            }

            observer?.Invoke(DocumentationPatchApplicationStage.BaselineCaptured, null);
            var resolution = resolver.Resolve(
                session,
                request,
                baseline,
                cancellationToken);
            if (resolution.Status != DocumentationPatchResolutionStatus.Resolved)
            {
                return new DocumentationPatchApplicationResult(
                    resolution.Status == DocumentationPatchResolutionStatus.Stale
                        ? DocumentationPatchApplicationStatus.Stale
                        : DocumentationPatchApplicationStatus.Rejected,
                    resolution.PrimaryCode,
                    resolution.PrimaryBlockId,
                    null);
            }

            observer?.Invoke(DocumentationPatchApplicationStage.ResolutionCompleted, null);
            var selectedBytes = RenderSelectedFiles(
                baseline,
                resolution,
                request,
                cancellationToken);
            observer?.Invoke(DocumentationPatchApplicationStage.RenderingCompleted, null);
            candidate = new DocumentationPatchCandidateWorkspaceBuilder(observer).Build(
                baseline,
                selectedBytes,
                cancellationToken);
            observer?.Invoke(
                DocumentationPatchApplicationStage.AfterSealBeforeReturn,
                candidate.RootPath);
            var result = new DocumentationPatchApplicationResult(
                DocumentationPatchApplicationStatus.Complete,
                null,
                null,
                candidate);
            candidate = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            candidate?.Dispose();
            throw;
        }
        catch (DocumentationPatchApplicationException exception)
        {
            candidate?.Dispose();
            return Failed(exception.Status, exception.Code);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or EncoderFallbackException
            or ArgumentException
            or NotSupportedException)
        {
            candidate?.Dispose();
            return Failed(
                DocumentationPatchApplicationStatus.Failure,
                "patch.rejected.unsafe-change");
        }
    }

    private static IReadOnlyDictionary<string, byte[]> RenderSelectedFiles(
        DocumentationPatchRepositoryBaseline baseline,
        DocumentationPatchResolutionResult resolution,
        DocumentationPatchRequest request,
        CancellationToken cancellationToken)
    {
        var blocks = request.Blocks.ToDictionary(
            block => block.BlockId,
            StringComparer.Ordinal);
        var result = new Dictionary<string, byte[]>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var group in resolution.Targets.GroupBy(
                     target => target.RepositoryPath,
                     OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = baseline.Entries.FirstOrDefault(candidate => string.Equals(
                candidate.RepositoryPath,
                group.Key,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
            if (entry is null)
            {
                throw Rejected();
            }

            var targets = group.ToImmutableArray();
            var firstBlock = blocks[targets[0].BlockId];
            if (firstBlock.Locator is not DocumentationPatchRepositoryLocator firstLocator)
            {
                throw Rejected();
            }

            var validated = DocumentationPatchValidator.ValidateRepositorySource(
                firstLocator,
                entry.Bytes.AsSpan());
            if (!validated.IsValid || validated.DecodedText is not { } source)
            {
                throw new DocumentationPatchApplicationException(
                    validated.Code?.StartsWith("patch.stale.", StringComparison.Ordinal) == true
                        ? DocumentationPatchApplicationStatus.Stale
                        : DocumentationPatchApplicationStatus.Rejected,
                    validated.Code ?? "patch.rejected.unsafe-change");
            }

            var newline = SelectNewline(source);
            var edits = ImmutableArray.CreateBuilder<DocumentationPatchTextEdit>(targets.Length);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = blocks[target.BlockId];
                if (block.Locator is not DocumentationPatchRepositoryLocator locator
                    || !string.Equals(locator.Path, target.RepositoryPath, StringComparison.Ordinal)
                    || locator.Encoding != target.Encoding
                    || !string.Equals(
                        locator.OriginalFileSha256,
                        entry.Sha256,
                        StringComparison.Ordinal))
                {
                    throw Rejected();
                }

                var ownerLineStart = LineStart(source, target.OwnerSpan.Start);
                var indentation = SliceIndentation(
                    source,
                    ownerLineStart,
                    target.OwnerSpan.Start);
                var rendered = DocumentationPatchRenderer.Render(
                    block.Content,
                    indentation,
                    newline);
                var (start, end) = block.EditKind switch
                {
                    DocumentationPatchEditKind.Insert
                        when target.DocumentationSpan is null =>
                        (ownerLineStart, ownerLineStart),
                    DocumentationPatchEditKind.Replace
                        when target.DocumentationSpan is { } documentation =>
                        ReplacementRegion(source, documentation, ownerLineStart),
                    _ => throw Rejected(),
                };
                edits.Add(new DocumentationPatchTextEdit(
                    start,
                    end,
                    rendered,
                    target.BlockId));
            }

            var ordered = edits.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index - 1].End > ordered[index].Start)
                {
                    throw Rejected();
                }
            }

            var candidate = source;
            foreach (var edit in ordered.OrderByDescending(edit => edit.Start))
            {
                candidate = string.Concat(
                    candidate.AsSpan(0, edit.Start),
                    edit.Replacement,
                    candidate.AsSpan(edit.End));
            }

            var encoded = Encode(candidate, firstLocator.Encoding);
            if (entry.Bytes.AsSpan().SequenceEqual(encoded))
            {
                throw Rejected();
            }

            result.Add(group.Key, encoded);
        }

        return result;
    }

    private static (int Start, int End) ReplacementRegion(
        string source,
        Utf16Span documentation,
        int ownerLineStart)
    {
        if (documentation.Start < 0
            || documentation.End < documentation.Start
            || documentation.End > source.Length
            || documentation.End > ownerLineStart)
        {
            throw Rejected();
        }

        var start = LineStart(source, documentation.Start);
        _ = SliceIndentation(source, start, documentation.Start);
        return (start, ownerLineStart);
    }

    private static int LineStart(string source, int position)
    {
        if (position < 0 || position > source.Length)
        {
            throw Rejected();
        }

        var newline = source.LastIndexOf('\n', Math.Max(0, position - 1));
        return newline < 0 ? 0 : newline + 1;
    }

    private static string SliceIndentation(string source, int start, int end)
    {
        if (start < 0 || end < start || end > source.Length)
        {
            throw Rejected();
        }

        var indentation = source[start..end];
        if (indentation.Any(character => character is not ' ' and not '\t'))
        {
            throw Rejected();
        }

        return indentation;
    }

    private static string SelectNewline(string source)
    {
        var hasLf = false;
        var hasCrLf = false;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 >= source.Length || source[index + 1] != '\n')
                {
                    throw Rejected();
                }

                hasCrLf = true;
                index++;
            }
            else if (source[index] == '\n')
            {
                hasLf = true;
            }
        }

        if (hasLf && hasCrLf)
        {
            throw Rejected();
        }

        return hasCrLf ? "\r\n" : "\n";
    }

    private static byte[] Encode(
        string source,
        DocumentationPatchRepositoryEncoding encoding)
    {
        var content = encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8 or
                DocumentationPatchRepositoryEncoding.Utf8Bom =>
                StrictUtf8.GetBytes(source),
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom =>
                StrictUtf16Le.GetBytes(source),
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom =>
                StrictUtf16Be.GetBytes(source),
            _ => throw Rejected(),
        };
        byte[] bom = encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8 => [],
            DocumentationPatchRepositoryEncoding.Utf8Bom => [0xef, 0xbb, 0xbf],
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => [0xff, 0xfe],
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => [0xfe, 0xff],
            _ => throw Rejected(),
        };
        return bom.Length == 0 ? content : [.. bom, .. content];
    }

    private static DocumentationPatchApplicationResult Failed(
        DocumentationPatchApplicationStatus status,
        string code) =>
        new(status, code, null, null);

    private static DocumentationPatchApplicationException Rejected() =>
        new(
            DocumentationPatchApplicationStatus.Rejected,
            "patch.rejected.unsafe-change");
}

internal sealed record DocumentationPatchTextEdit(
    int Start,
    int End,
    string Replacement,
    string BlockId);

internal sealed class DocumentationPatchApplicationException : Exception
{
    public DocumentationPatchApplicationException(
        DocumentationPatchApplicationStatus status,
        string code)
    {
        Status = status;
        Code = code;
    }

    public DocumentationPatchApplicationStatus Status { get; }

    public string Code { get; }
}
