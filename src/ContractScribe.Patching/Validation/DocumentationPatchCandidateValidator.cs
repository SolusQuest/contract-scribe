using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Patching.Resolution;
using ContractScribe.Roslyn;

namespace ContractScribe.Patching.Validation;

internal sealed record DocumentationPatchCandidateValidationDecision(
    bool IsAccepted,
    string? FailureCode,
    string? FailureBlockId,
    ImmutableArray<DocumentationPatchChangedFileInput> ChangedFiles,
    ImmutableArray<DocumentationPatchInvariantResult> Invariants,
    DocumentationPatchCandidateValidationResult? RoslynEvidence);

internal static class DocumentationPatchCandidateValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static DocumentationPatchCandidateValidationDecision Validate(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        DocumentationPatchRepositoryBaseline baseline,
        DocumentationPatchResolutionResult resolution,
        ImmutableArray<CandidateWorkspaceFile> captured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(resolution);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (resolution.Status != DocumentationPatchResolutionStatus.Resolved
                || resolution.Targets.Length != request.Blocks.Length
                || captured.IsDefault
                || captured.Length != baseline.Entries.Length)
            {
                return Rejected("patch.rejected.unsafe-change", null);
            }

            var capturedByPath = captured.ToDictionary(
                file => file.RepositoryPath,
                PathComparer);
            if (capturedByPath.Count != captured.Length
                || baseline.Entries.Any(entry => !capturedByPath.ContainsKey(entry.RepositoryPath)))
            {
                return Rejected("patch.rejected.unsafe-change", null);
            }

            var plans = BuildPlans(request, resolution, baseline, cancellationToken);
            foreach (var plan in plans.Values.OrderBy(
                         plan => plan.RepositoryPath,
                         StringComparer.Ordinal))
            {
                foreach (var edit in plan.Edits)
                {
                    if (edit.End > edit.Start
                        && CountDocumentationLines(
                            plan.OriginalText.AsSpan(edit.Start, edit.End - edit.Start)) == 0)
                    {
                        return Rejected("patch.rejected.unsafe-change", edit.BlockId);
                    }
                }
            }

            var changedPaths = plans.Keys.ToHashSet(PathComparer);
            foreach (var entry in baseline.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = capturedByPath[entry.RepositoryPath];
                if (!changedPaths.Contains(entry.RepositoryPath)
                    && !entry.Bytes.AsSpan().SequenceEqual(candidate.Bytes.AsSpan()))
                {
                    return Rejected("patch.rejected.unsafe-change", null);
                }
            }

            foreach (var plan in plans.Values)
            {
                var candidate = capturedByPath[plan.RepositoryPath];
                if (!plan.ExpectedBytes.AsSpan().SequenceEqual(candidate.Bytes.AsSpan())
                    || !TryDecode(candidate.Bytes.AsSpan(), plan.Encoding, out var candidateText)
                    || !string.Equals(candidateText, plan.ExpectedText, StringComparison.Ordinal)
                    || !ProveReboundIdempotency(plan, candidateText))
                {
                    return Rejected("patch.rejected.unsafe-change", plan.FirstBlockId);
                }
            }

            var roslynInputs = plans.Values.OrderBy(plan => plan.RepositoryPath, StringComparer.Ordinal)
                .Select(plan => new DocumentationPatchCandidateValidationFile(
                    plan.RepositoryPath,
                    plan.Encoding,
                    capturedByPath[plan.RepositoryPath].Bytes))
                .ToImmutableArray();
            var roslyn = DocumentationPatchCandidateValidation.Validate(
                session,
                baseline,
                roslynInputs,
                cancellationToken);
            if (!roslyn.IsValid)
            {
                return Rejected(
                    roslyn.FailureCode ?? "patch.rejected.unsafe-change",
                    null,
                    roslyn);
            }

            var changedFiles = plans.Values.OrderBy(plan => plan.RepositoryPath, StringComparer.Ordinal)
                .Select(plan =>
                {
                    var candidate = capturedByPath[plan.RepositoryPath];
                    return new DocumentationPatchChangedFileInput(
                        plan.RepositoryPath,
                        plan.OriginalSha256,
                        Sha256(candidate.Bytes.AsSpan()),
                        plan.Edits.Length,
                        plan.Edits.Sum(edit => EncodedLength(
                            plan.OriginalText.AsSpan(edit.Start, edit.End - edit.Start),
                            plan.Encoding)),
                        plan.Edits.Sum(edit => EncodedLength(
                            edit.Rendered.AsSpan(),
                            plan.Encoding)),
                        plan.Edits.Sum(edit => CountDocumentationLines(
                            plan.OriginalText.AsSpan(edit.Start, edit.End - edit.Start))),
                        plan.Edits.Sum(edit => CountDocumentationLines(edit.Rendered.AsSpan())));
                })
                .ToImmutableArray();
            return new DocumentationPatchCandidateValidationDecision(
                true,
                null,
                null,
                changedFiles,
                PassedInvariants(),
                roslyn);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CandidateValidationException exception)
        {
            return Rejected("patch.rejected.unsafe-change", exception.BlockId);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or DecoderFallbackException
            or EncoderFallbackException
            or OverflowException)
        {
            return Rejected("patch.rejected.unsafe-change", null);
        }
    }

    internal static bool TryPlanRepresentationFailure(
        DocumentationPatchRequest request,
        DocumentationPatchResolutionResult resolution,
        DocumentationPatchRepositoryBaseline baseline,
        CancellationToken cancellationToken,
        out string? blockId)
    {
        try
        {
            _ = BuildPlans(request, resolution, baseline, cancellationToken);
            blockId = null;
            return false;
        }
        catch (CandidateValidationException exception)
        {
            blockId = exception.BlockId;
            return blockId is not null;
        }
    }

    private static Dictionary<string, FilePlan> BuildPlans(
        DocumentationPatchRequest request,
        DocumentationPatchResolutionResult resolution,
        DocumentationPatchRepositoryBaseline baseline,
        CancellationToken cancellationToken)
    {
        var blocks = request.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        var plans = new Dictionary<string, FilePlan>(PathComparer);
        foreach (var group in resolution.Targets.GroupBy(target => target.RepositoryPath, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = baseline.Entries.SingleOrDefault(candidate =>
                PathComparer.Equals(candidate.RepositoryPath, group.Key))
                ?? throw new CandidateValidationException(group.First().BlockId);
            var targets = group.ToImmutableArray();
            var firstBlock = blocks[targets[0].BlockId];
            if (firstBlock.Locator is not DocumentationPatchRepositoryLocator firstLocator
                || !TryDecode(entry.Bytes.AsSpan(), firstLocator.Encoding, out var source))
            {
                throw new CandidateValidationException(firstBlock.BlockId);
            }

            var newline = SelectNewline(source, firstBlock.BlockId);
            var edits = ImmutableArray.CreateBuilder<TextEditPlan>(targets.Length);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = blocks[target.BlockId];
                if (block.Locator is not DocumentationPatchRepositoryLocator locator
                    || !PathComparer.Equals(locator.Path, target.RepositoryPath)
                    || locator.Encoding != target.Encoding
                    || !string.Equals(locator.OriginalFileSha256, entry.Sha256, StringComparison.Ordinal))
                {
                    throw new CandidateValidationException(block.BlockId);
                }

                var ownerLineStart = LineStart(source, target.OwnerSpan.Start, block.BlockId);
                var indentation = SliceIndentation(
                    source,
                    ownerLineStart,
                    target.OwnerSpan.Start,
                    block.BlockId);
                var rendered = DocumentationPatchRenderer.Render(
                    block.Content,
                    indentation,
                    newline);
                var (start, end) = block.EditKind switch
                {
                    DocumentationPatchEditKind.Insert when target.DocumentationSpan is null =>
                        (ownerLineStart, ownerLineStart),
                    DocumentationPatchEditKind.Replace when target.DocumentationSpan is { } documentation =>
                        ReplacementRegion(source, documentation, ownerLineStart, block.BlockId),
                    _ => throw new CandidateValidationException(block.BlockId),
                };
                edits.Add(new TextEditPlan(start, end, rendered, block.BlockId));
            }

            var ordered = edits.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToImmutableArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index - 1].End > ordered[index].Start)
                {
                    throw new CandidateValidationException(ordered[index].BlockId);
                }
            }

            var candidate = source;
            foreach (var edit in ordered.OrderByDescending(edit => edit.Start))
            {
                candidate = string.Concat(
                    candidate.AsSpan(0, edit.Start),
                    edit.Rendered,
                    candidate.AsSpan(edit.End));
            }

            var encoded = Encode(candidate, firstLocator.Encoding);
            if (entry.Bytes.AsSpan().SequenceEqual(encoded))
            {
                throw new CandidateValidationException(firstBlock.BlockId);
            }

            plans.Add(group.Key, new FilePlan(
                group.Key,
                entry.Sha256,
                firstLocator.Encoding,
                source,
                candidate,
                ImmutableArray.CreateRange(encoded),
                ordered));
        }

        return plans;
    }

    private static DocumentationPatchCandidateValidationDecision Rejected(
        string code,
        string? blockId,
        DocumentationPatchCandidateValidationResult? roslyn = null) =>
        new(false, code, blockId, [], RootFailureInvariants(), roslyn);

    internal static ImmutableArray<DocumentationPatchInvariantResult> PassedInvariants() =>
        DocumentationPatchValidator.InvariantIds.Select(id =>
            new DocumentationPatchInvariantResult(id, DocumentationPatchInvariantStatus.Passed))
            .ToImmutableArray();

    internal static ImmutableArray<DocumentationPatchInvariantResult> RootFailureInvariants() =>
        DocumentationPatchValidator.InvariantIds.Select((id, index) =>
            new DocumentationPatchInvariantResult(
                id,
                index == DocumentationPatchValidator.InvariantIds.Length - 1
                    ? DocumentationPatchInvariantStatus.Passed
                    : DocumentationPatchInvariantStatus.NotRun))
            .ToImmutableArray();

    private static bool ProveReboundIdempotency(FilePlan plan, string candidate)
    {
        var delta = 0;
        var rebound = ImmutableArray.CreateBuilder<TextEditPlan>(plan.Edits.Length);
        foreach (var edit in plan.Edits)
        {
            var start = checked(edit.Start + delta);
            if (start < 0
                || start + edit.Rendered.Length > candidate.Length
                || !candidate.AsSpan(start, edit.Rendered.Length)
                    .SequenceEqual(edit.Rendered.AsSpan()))
            {
                return false;
            }

            rebound.Add(new TextEditPlan(
                start,
                checked(start + edit.Rendered.Length),
                edit.Rendered,
                edit.BlockId));
            delta = checked(delta + edit.Rendered.Length - (edit.End - edit.Start));
        }

        var replay = candidate;
        foreach (var edit in rebound.OrderByDescending(edit => edit.Start))
        {
            replay = string.Concat(
                replay.AsSpan(0, edit.Start),
                edit.Rendered,
                replay.AsSpan(edit.End));
        }

        return string.Equals(replay, candidate, StringComparison.Ordinal)
            && Encode(replay, plan.Encoding).AsSpan()
                .SequenceEqual(plan.ExpectedBytes.AsSpan());
    }

    private static (int Start, int End) ReplacementRegion(
        string source,
        Utf16Span documentation,
        int ownerLineStart,
        string blockId)
    {
        if (documentation.Start < 0
            || documentation.End < documentation.Start
            || documentation.End > source.Length
            || documentation.End > ownerLineStart)
        {
            throw new CandidateValidationException(blockId);
        }

        var start = LineStart(source, documentation.Start, blockId);
        _ = SliceIndentation(source, start, documentation.Start, blockId);
        return (start, ownerLineStart);
    }

    private static int LineStart(string source, int position, string blockId)
    {
        if (position < 0 || position > source.Length)
        {
            throw new CandidateValidationException(blockId);
        }

        var newline = source.LastIndexOf('\n', Math.Max(0, position - 1));
        return newline < 0 ? 0 : newline + 1;
    }

    private static string SliceIndentation(
        string source,
        int start,
        int end,
        string blockId)
    {
        if (start < 0 || end < start || end > source.Length)
        {
            throw new CandidateValidationException(blockId);
        }

        var indentation = source[start..end];
        if (indentation.Any(character => character is not ' ' and not '\t'))
        {
            throw new CandidateValidationException(blockId);
        }

        return indentation;
    }

    private static string SelectNewline(string source, string blockId)
    {
        var hasLf = false;
        var hasCrLf = false;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 >= source.Length || source[index + 1] != '\n')
                {
                    throw new CandidateValidationException(blockId);
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
            throw new CandidateValidationException(blockId);
        }

        return hasCrLf ? "\r\n" : "\n";
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        DocumentationPatchRepositoryEncoding encoding,
        out string result)
    {
        try
        {
            result = encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8 when !HasBom(bytes) =>
                    StrictUtf8.GetString(bytes),
                DocumentationPatchRepositoryEncoding.Utf8Bom
                    when HasPrefix(bytes, 0xef, 0xbb, 0xbf) =>
                    StrictUtf8.GetString(bytes[3..]),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom
                    when HasPrefix(bytes, 0xff, 0xfe) =>
                    StrictUtf16Le.GetString(bytes[2..]),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom
                    when HasPrefix(bytes, 0xfe, 0xff) =>
                    StrictUtf16Be.GetString(bytes[2..]),
                _ => throw new DecoderFallbackException(),
            };
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = string.Empty;
            return false;
        }
    }

    private static byte[] Encode(
        string source,
        DocumentationPatchRepositoryEncoding encoding)
    {
        var content = EncodeContent(source.AsSpan(), encoding);
        ReadOnlySpan<byte> bom = encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8 => [],
            DocumentationPatchRepositoryEncoding.Utf8Bom => [0xef, 0xbb, 0xbf],
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => [0xff, 0xfe],
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => [0xfe, 0xff],
            _ => throw new EncoderFallbackException(),
        };
        return bom.IsEmpty ? content : [.. bom, .. content];
    }

    private static byte[] EncodeContent(
        ReadOnlySpan<char> text,
        DocumentationPatchRepositoryEncoding encoding) => encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8
                or DocumentationPatchRepositoryEncoding.Utf8Bom =>
                StrictUtf8.GetBytes(text.ToString()),
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom =>
                StrictUtf16Le.GetBytes(text.ToString()),
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom =>
                StrictUtf16Be.GetBytes(text.ToString()),
            _ => throw new EncoderFallbackException(),
        };

    private static int EncodedLength(
        ReadOnlySpan<char> text,
        DocumentationPatchRepositoryEncoding encoding) =>
        EncodeContent(text, encoding).Length;

    private static int CountDocumentationLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index != text.Length && text[index] != '\n')
            {
                continue;
            }

            var line = text[start..index];
            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var offset = 0;
            while (offset < line.Length && line[offset] is ' ' or '\t')
            {
                offset++;
            }

            if (line[offset..].StartsWith("///".AsSpan(), StringComparison.Ordinal))
            {
                count++;
            }

            start = index + 1;
        }

        return count;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool HasBom(ReadOnlySpan<byte> bytes) =>
        HasPrefix(bytes, 0xef, 0xbb, 0xbf)
        || HasPrefix(bytes, 0xff, 0xfe)
        || HasPrefix(bytes, 0xfe, 0xff);

    private static bool HasPrefix(
        ReadOnlySpan<byte> bytes,
        byte first,
        byte second,
        byte? third = null) =>
        bytes.Length >= (third.HasValue ? 3 : 2)
        && bytes[0] == first
        && bytes[1] == second
        && (!third.HasValue || bytes[2] == third.Value);

    private sealed record FilePlan(
        string RepositoryPath,
        string OriginalSha256,
        DocumentationPatchRepositoryEncoding Encoding,
        string OriginalText,
        string ExpectedText,
        ImmutableArray<byte> ExpectedBytes,
        ImmutableArray<TextEditPlan> Edits)
    {
        public string FirstBlockId => Edits[0].BlockId;
    }

    private sealed record TextEditPlan(
        int Start,
        int End,
        string Rendered,
        string BlockId);

    private sealed class CandidateValidationException(string? blockId) : Exception
    {
        public string? BlockId { get; } = blockId;
    }
}
