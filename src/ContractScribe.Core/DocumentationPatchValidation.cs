using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class DocumentationPatchValidator
{
    public const int MaximumArtifactUtf8Bytes = 1_048_576;
    public const int MaximumLogicalLineScalars = 2_048;
    public const int MaximumBlockTextScalars = 32_768;

    public static ImmutableArray<string> InvariantIds { get; } =
    [
        "patch.invariant.non-documentation-tokens-unchanged",
        "patch.invariant.selected-documentation-only",
        "patch.invariant.no-new-parse-diagnostics",
        "patch.invariant.symbol-semantics-unchanged",
        "patch.invariant.repository-scope",
        "patch.invariant.file-representation-preserved",
        "patch.invariant.idempotent",
        "patch.invariant.traceable",
        "patch.invariant.fail-closed",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> ResultDiagnosticCodes =
    [
        "patch.stale.repository-context",
        "patch.stale.input-identity",
        "patch.stale.target-profile",
        "patch.stale.compilation-context",
        "patch.stale.source-bytes",
        "patch.stale.source-encoding",
        "patch.stale.source-span",
        "patch.rejected.unsupported-target",
        "patch.rejected.ambiguous-target",
        "patch.rejected.non-writable-target",
        "patch.rejected.edit-state",
        "patch.rejected.unsafe-change",
        "patch.rejected.no-effective-change",
    ];

    public static DocumentationPatchRequestParseResult ParseRequest(
        ReadOnlyMemory<byte> utf8Json)
    {
        var rawFailure = TryParseArtifact(
            utf8Json,
            "patch.request",
            "patchRequestVersion",
            out var document);
        if (rawFailure is not null)
        {
            return new DocumentationPatchRequestParseResult(null, rawFailure);
        }

        using (document)
        {
            try
            {
                var root = document!.RootElement;
                ExpectProperties(
                    root,
                    string.Empty,
                    "patchRequestVersion",
                    "context",
                    "provenanceCatalog",
                    "blocks");

                ContractFailure? selectedFailure = null;
                DocumentationPatchContext? context = null;
                try
                {
                    context = ParseContext(root.GetProperty("context"), "/context");
                }
                catch (ContractFailure failure)
                {
                    selectedFailure = SelectRequestFailure(selectedFailure, failure);
                }

                var catalog = ImmutableArray<string>.Empty;
                try
                {
                    catalog = ParseOrderedIds(
                        root.GetProperty("provenanceCatalog"),
                        "/provenanceCatalog",
                        4_096,
                        allowEmpty: true);
                }
                catch (ContractFailure failure)
                {
                    selectedFailure = SelectRequestFailure(selectedFailure, failure);
                }

                var blocksElement = root.GetProperty("blocks");
                var blocks = ImmutableArray.CreateBuilder<DocumentationPatchBlockRequest>();
                var blockIds = new HashSet<string>(StringComparer.Ordinal);
                var symbols = new HashSet<string>(StringComparer.Ordinal);
                var locators = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    ExpectArray(blocksElement, "/blocks", 1, 512);
                    var index = 0;
                    foreach (var element in blocksElement.EnumerateArray())
                    {
                        var pointer = $"/blocks/{index}";
                        DocumentationPatchBlockRequest? block = null;
                        try
                        {
                            block = ParseBlock(element, pointer);
                        }
                        catch (ContractFailure failure)
                        {
                            selectedFailure = SelectRequestFailure(selectedFailure, failure);
                        }

                        if (block is not null)
                        {
                            if (!blockIds.Add(block.BlockId))
                            {
                                selectedFailure = SelectRequestFailure(
                                    selectedFailure,
                                    Fail("invalid-order", pointer + "/blockId"));
                            }

                            var symbolKey = block.SymbolRef.CompilationContextRef
                                + "\u0000"
                                + block.SymbolRef.DocumentationCommentId;
                            if (!symbols.Add(symbolKey)
                                || !locators.Add(GetLocatorBindingKey(block.Locator)))
                            {
                                selectedFailure = SelectRequestFailure(
                                    selectedFailure,
                                    Fail("invalid-order", pointer));
                            }

                            if (blocks.Count > 0 && CompareBlocks(blocks[^1], block) >= 0)
                            {
                                selectedFailure = SelectRequestFailure(
                                    selectedFailure,
                                    Fail("invalid-order", pointer));
                            }

                            blocks.Add(block);
                        }

                        index++;
                    }
                }
                catch (ContractFailure failure)
                {
                    selectedFailure = SelectRequestFailure(selectedFailure, failure);
                }

                for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    foreach (var provenanceRef in blocks[blockIndex].ProvenanceRefs)
                    {
                        if (catalog.BinarySearch(provenanceRef, StringComparer.Ordinal) < 0)
                        {
                            selectedFailure = SelectRequestFailure(
                                selectedFailure,
                                Fail(
                                    "invalid-reference",
                                    $"/blocks/{blockIndex}/provenanceRefs"));
                        }
                    }
                }

                if (selectedFailure is not null)
                {
                    throw selectedFailure;
                }

                return new DocumentationPatchRequestParseResult(
                    new DocumentationPatchRequest(
                        Convert.ToHexString(SHA256.HashData(utf8Json.Span)).ToLowerInvariant(),
                        context!,
                        catalog,
                        blocks.ToImmutable()),
                    null);
            }
            catch (ContractFailure failure)
            {
                return new DocumentationPatchRequestParseResult(
                    null,
                    new PatchRequestValidationFailure(
                        "patch.request." + failure.Category,
                        failure.Pointer));
            }
        }
    }

    public static DocumentationPatchResultParseResult ParseValidationResult(
        ReadOnlyMemory<byte> utf8Json)
    {
        var rawFailure = TryParseResultArtifact(
            utf8Json,
            "patchValidationResultVersion",
            out var document);
        if (rawFailure is not null)
        {
            return new DocumentationPatchResultParseResult(null, rawFailure);
        }

        using (document)
        {
            try
            {
                var root = document!.RootElement;
                ExpectProperties(
                    root,
                    string.Empty,
                    "patchValidationResultVersion",
                    "patchRequestSha256",
                    "context",
                    "outcome",
                    "targets",
                    "changedFiles",
                    "changedDocumentationBlockCount",
                    "invariants",
                    "diagnostics");

                var patchRequestSha256 = ReadString(
                    root,
                    "patchRequestSha256",
                    string.Empty,
                    64);
                if (!IsSha256(patchRequestSha256))
                {
                    throw Fail("invalid-vocabulary", "/patchRequestSha256");
                }

                var context = ParseContext(root.GetProperty("context"), "/context");
                var outcome = ParseOutcome(ReadString(root, "outcome", string.Empty, 16));
                var targets = ParseTargets(root.GetProperty("targets"), "/targets");
                var changedFiles = ParseChangedFiles(
                    root.GetProperty("changedFiles"),
                    "/changedFiles");
                var changedBlockCount = ReadCount(
                    root,
                    "changedDocumentationBlockCount",
                    string.Empty);
                var invariants = ParseInvariants(
                    root.GetProperty("invariants"),
                    "/invariants");
                var diagnostics = ParseDiagnostics(
                    root.GetProperty("diagnostics"),
                    "/diagnostics");

                return new DocumentationPatchResultParseResult(
                    new DocumentationPatchValidationResult(
                        patchRequestSha256,
                        context,
                        outcome,
                        targets,
                        changedFiles,
                        changedBlockCount,
                        invariants,
                        diagnostics),
                    null);
            }
            catch (ContractFailure failure)
            {
                return new DocumentationPatchResultParseResult(
                    null,
                    new PatchResultValidationFailure(
                        "patch.result." + MapResultCategory(failure.Category),
                        failure.Pointer));
            }
        }
    }

    public static DocumentationPatchValidationCheck ValidateContext(
        DocumentationPatchRequest request,
        DocumentationPatchValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.Context.RepositoryContextRef != context.RepositoryContextRef)
        {
            return Invalid("patch.stale.repository-context");
        }

        if (!string.Equals(
                request.Context.InputIdentity,
                context.InputIdentity,
                StringComparison.Ordinal))
        {
            return Invalid("patch.stale.input-identity");
        }

        if (request.Context.TargetProfile != context.TargetProfile)
        {
            return Invalid("patch.stale.target-profile");
        }

        foreach (var block in request.Blocks)
        {
            if (!context.CompilationContextRefs.Contains(
                    block.SymbolRef.CompilationContextRef))
            {
                return Invalid(
                    "patch.stale.compilation-context",
                    block.BlockId);
            }
        }

        return Valid();
    }

    public static DocumentationPatchValidationCheck ValidateRepositorySource(
        DocumentationPatchRepositoryLocator locator,
        ReadOnlySpan<byte> exactFileBytes)
    {
        ArgumentNullException.ThrowIfNull(locator);

        string decoded;
        try
        {
            decoded = locator.Encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8
                    when !HasAnyBom(exactFileBytes) =>
                    StrictUtf8.GetString(exactFileBytes),
                DocumentationPatchRepositoryEncoding.Utf8Bom
                    when HasPrefix(exactFileBytes, 0xef, 0xbb, 0xbf) =>
                    StrictUtf8.GetString(exactFileBytes[3..]),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom
                    when HasPrefix(exactFileBytes, 0xff, 0xfe) =>
                    StrictUtf16Le.GetString(exactFileBytes[2..]),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom
                    when HasPrefix(exactFileBytes, 0xfe, 0xff) =>
                    StrictUtf16Be.GetString(exactFileBytes[2..]),
                _ => throw new DecoderFallbackException(),
            };
        }
        catch (DecoderFallbackException)
        {
            return Invalid("patch.stale.source-encoding");
        }

        var digest = Convert.ToHexString(SHA256.HashData(exactFileBytes)).ToLowerInvariant();
        if (!string.Equals(digest, locator.OriginalFileSha256, StringComparison.Ordinal))
        {
            return Invalid("patch.stale.source-bytes");
        }

        if (!IsValidSpan(locator.DeclarationSpan, decoded.Length))
        {
            return Invalid("patch.stale.source-span");
        }

        return Valid(decoded);
    }

    public static DocumentationPatchValidationCheck ValidateGeneratedSource(
        DocumentationPatchGeneratedLocator locator,
        string exactSourceText)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(exactSourceText);

        if (!TryCountScalars(exactSourceText, out _))
        {
            return Invalid("patch.stale.source-encoding");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(exactSourceText);
        }
        catch (EncoderFallbackException)
        {
            return Invalid("patch.stale.source-encoding");
        }

        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, locator.SourceSha256, StringComparison.Ordinal))
        {
            return Invalid("patch.stale.source-bytes");
        }

        if (!IsValidSpan(locator.DeclarationSpan, exactSourceText.Length))
        {
            return Invalid("patch.stale.source-span");
        }

        return Valid(exactSourceText);
    }

    public static DocumentationPatchValidationCheck ValidateResult(
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (!string.Equals(
                request.ArtifactSha256,
                result.PatchRequestSha256,
                StringComparison.Ordinal)
            || request.Context != result.Context
            || request.Blocks.Length != result.Targets.Length)
        {
            return Invalid("patch.result.invalid-correlation");
        }

        for (var index = 0; index < request.Blocks.Length; index++)
        {
            var block = request.Blocks[index];
            var trace = result.Targets[index];
            if (!string.Equals(block.BlockId, trace.BlockId, StringComparison.Ordinal)
                || block.SymbolRef != trace.SymbolRef
                || block.Locator != trace.Locator
                || !block.ProvenanceRefs.SequenceEqual(trace.ProvenanceRefs, StringComparer.Ordinal))
            {
                return Invalid("patch.result.invalid-correlation", block.BlockId);
            }
        }

        var repositoryBlocks = request.Blocks
            .Where(block => block.Locator is DocumentationPatchRepositoryLocator)
            .GroupBy(
                block => ((DocumentationPatchRepositoryLocator)block.Locator).Path,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.Ordinal);
        long blockSum = 0;
        var acceptedObservationsAreValid = true;
        foreach (var file in result.ChangedFiles)
        {
            if (!repositoryBlocks.TryGetValue(file.Path, out var pathBlocks))
            {
                return Invalid("patch.result.invalid-correlation");
            }

            var expectedEncoding = ((DocumentationPatchRepositoryLocator)pathBlocks[0].Locator)
                .Encoding;
            if (!pathBlocks.All(block =>
                {
                    var locator = (DocumentationPatchRepositoryLocator)block.Locator;
                    return string.Equals(
                            locator.OriginalFileSha256,
                            file.OriginalFileSha256,
                            StringComparison.Ordinal)
                        && locator.Encoding == expectedEncoding;
                }))
            {
                return Invalid("patch.result.invalid-correlation");
            }

            blockSum += file.ChangedDocumentationBlockCount;
            var hasReplacement = pathBlocks.Any(
                block => block.EditKind == DocumentationPatchEditKind.Replace);
            acceptedObservationsAreValid &=
                file.ChangedDocumentationBlockCount == pathBlocks.Length
                && file.CandidateDocumentationByteCount > 0
                && file.CandidateDocumentationLineCount > 0
                && (hasReplacement
                    ? file.OriginalDocumentationByteCount > 0
                        && file.OriginalDocumentationLineCount > 0
                    : file.OriginalDocumentationByteCount == 0
                        && file.OriginalDocumentationLineCount == 0);
        }

        if (blockSum != result.ChangedDocumentationBlockCount)
        {
            return Invalid("patch.result.invalid-correlation");
        }

        var diagnosticCheck = ValidateDiagnostics(request, result);
        if (!diagnosticCheck.IsValid)
        {
            return diagnosticCheck;
        }

        var hasError = result.Diagnostics.Any(
            diagnostic => diagnostic.Severity == DocumentationPatchDiagnosticSeverity.Error);
        var hasStaleDiagnostic = result.Diagnostics.Any(
            diagnostic => diagnostic.Code.StartsWith("patch.stale.", StringComparison.Ordinal));
        var hasRejectedDiagnostic = result.Diagnostics.Any(
            diagnostic => diagnostic.Code.StartsWith("patch.rejected.", StringComparison.Ordinal));
        var primaryDiagnosticCode = result.Diagnostics.IsEmpty
            ? null
            : result.Diagnostics[0].Code;

        switch (result.Outcome)
        {
            case DocumentationPatchOutcome.Accepted:
                if (result.Targets.Any(
                        trace => trace.Status != DocumentationPatchTargetStatus.Valid)
                    || result.Invariants.Any(
                        invariant => invariant.Status != DocumentationPatchInvariantStatus.Passed)
                    || hasError
                    || hasStaleDiagnostic
                    || hasRejectedDiagnostic
                    || result.ChangedFiles.IsEmpty
                    || result.ChangedDocumentationBlockCount <= 0
                    || result.ChangedDocumentationBlockCount != request.Blocks.Length
                    || result.ChangedFiles.Length != repositoryBlocks.Count
                    || !acceptedObservationsAreValid
                    || result.ChangedFiles.Any(
                        file => string.Equals(
                            file.OriginalFileSha256,
                            file.CandidateFileSha256,
                            StringComparison.Ordinal))
                    || request.Blocks.Any(
                        block => block.Locator.Kind != DocumentationPatchSourceKind.Repository))
                {
                    return Invalid("patch.result.invalid-outcome");
                }

                break;
            case DocumentationPatchOutcome.Stale:
                if (!result.ChangedFiles.IsEmpty
                    || result.ChangedDocumentationBlockCount != 0
                    || primaryDiagnosticCode is null
                    || !primaryDiagnosticCode.StartsWith("patch.stale.", StringComparison.Ordinal)
                    || !result.Targets.Any(trace => trace.Status is
                        DocumentationPatchTargetStatus.Stale
                        or DocumentationPatchTargetStatus.NotEvaluated)
                    || primaryDiagnosticCode is
                        "patch.stale.repository-context"
                        or "patch.stale.input-identity"
                        or "patch.stale.target-profile"
                        && result.Targets.Any(
                            trace => trace.Status != DocumentationPatchTargetStatus.NotEvaluated))
                {
                    return Invalid("patch.result.invalid-outcome");
                }

                break;
            case DocumentationPatchOutcome.Rejected:
                if (!result.ChangedFiles.IsEmpty
                    || result.ChangedDocumentationBlockCount != 0
                    || primaryDiagnosticCode is null
                    || !primaryDiagnosticCode.StartsWith("patch.rejected.", StringComparison.Ordinal)
                    || result.Targets.Any(
                        trace => trace.Status == DocumentationPatchTargetStatus.Stale)
                    || hasStaleDiagnostic)
                {
                    return Invalid("patch.result.invalid-outcome");
                }

                break;
            default:
                return Invalid("patch.result.invalid-outcome");
        }

        return Valid();
    }

    private static DocumentationPatchValidationCheck ValidateDiagnostics(
        DocumentationPatchRequest request,
        DocumentationPatchValidationResult result)
    {
        if (result.Diagnostics.IsEmpty)
        {
            return Valid();
        }

        var blockIndexes = request.Blocks
            .Select((block, index) => (block.BlockId, Index: index))
            .ToDictionary(item => item.BlockId, item => item.Index, StringComparer.Ordinal);
        var diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
        DocumentationPatchDiagnostic? previousSecondary = null;
        var primaryIndex = 0;
        for (var index = 0; index < result.Diagnostics.Length; index++)
        {
            var diagnostic = result.Diagnostics[index];
            var key = diagnostic.Code
                + "\u0000"
                + diagnostic.BlockId
                + "\u0000"
                + diagnostic.Path
                + "\u0000"
                + diagnostic.Pointer;
            if (!diagnosticKeys.Add(key)
                || index > 1 && CompareSecondaryDiagnostics(previousSecondary!, diagnostic) >= 0)
            {
                return Invalid("patch.result.invalid-outcome");
            }

            if (index > 0)
            {
                previousSecondary = diagnostic;
            }

            if (ComparePrimaryDiagnostics(
                    diagnostic,
                    result.Diagnostics[primaryIndex],
                    blockIndexes) < 0)
            {
                primaryIndex = index;
            }

            var rootContextFailure = IsRootContextDiagnostic(diagnostic.Code);
            var noEffectiveChange = diagnostic.Code == "patch.rejected.no-effective-change";
            if (rootContextFailure || noEffectiveChange)
            {
                if (diagnostic.BlockId is not null || diagnostic.Path is not null)
                {
                    return Invalid("patch.result.invalid-correlation");
                }

                if (rootContextFailure && result.Targets.Any(
                        target => target.Status != DocumentationPatchTargetStatus.NotEvaluated)
                    || noEffectiveChange && result.Targets.Any(
                        target => target.Status != DocumentationPatchTargetStatus.Valid))
                {
                    return Invalid("patch.result.invalid-outcome");
                }

                continue;
            }

            if (diagnostic.BlockId is null
                || !blockIndexes.TryGetValue(diagnostic.BlockId, out var blockIndex))
            {
                return Invalid("patch.result.invalid-correlation");
            }

            var block = request.Blocks[blockIndex];
            var target = result.Targets[blockIndex];
            var expectedStatus = diagnostic.Code.StartsWith("patch.stale.", StringComparison.Ordinal)
                ? DocumentationPatchTargetStatus.Stale
                : DocumentationPatchTargetStatus.Invalid;
            if (target.Status != expectedStatus)
            {
                return Invalid("patch.result.invalid-outcome", block.BlockId);
            }

            if (diagnostic.Path is not null
                && (block.Locator is not DocumentationPatchRepositoryLocator repository
                    || !string.Equals(repository.Path, diagnostic.Path, StringComparison.Ordinal)))
            {
                return Invalid("patch.result.invalid-correlation", block.BlockId);
            }
        }

        return primaryIndex == 0
            ? Valid()
            : Invalid("patch.result.invalid-outcome");
    }

    private static int ComparePrimaryDiagnostics(
        DocumentationPatchDiagnostic left,
        DocumentationPatchDiagnostic right,
        IReadOnlyDictionary<string, int> blockIndexes)
    {
        var comparison = GetDiagnosticCategory(left.Code).CompareTo(GetDiagnosticCategory(right.Code));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = GetDiagnosticBlockIndex(left, blockIndexes)
            .CompareTo(GetDiagnosticBlockIndex(right, blockIndexes));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = GetDiagnosticCodePrecedence(left.Code)
            .CompareTo(GetDiagnosticCodePrecedence(right.Code));
        return comparison != 0 ? comparison : CompareSecondaryDiagnostics(left, right);
    }

    private static int CompareSecondaryDiagnostics(
        DocumentationPatchDiagnostic left,
        DocumentationPatchDiagnostic right)
    {
        var comparison = string.CompareOrdinal(left.Code, right.Code);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(left.BlockId, right.BlockId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(left.Path, right.Path);
        return comparison != 0
            ? comparison
            : string.CompareOrdinal(left.Pointer, right.Pointer);
    }

    private static int GetDiagnosticCategory(string code) => code switch
    {
        "patch.stale.repository-context"
            or "patch.stale.input-identity"
            or "patch.stale.target-profile" => 0,
        _ when code.StartsWith("patch.stale.", StringComparison.Ordinal) => 1,
        "patch.rejected.no-effective-change" => 3,
        _ => 2,
    };

    private static ContractFailure SelectRequestFailure(
        ContractFailure? current,
        ContractFailure candidate) => current is null
        || GetRequestFailurePrecedence(candidate.Category)
            < GetRequestFailurePrecedence(current.Category)
            ? candidate
            : current;

    private static bool TryParseRequestValue<T>(
        Func<T> parser,
        ref ContractFailure? selectedFailure,
        out T value)
    {
        try
        {
            value = parser();
            return true;
        }
        catch (ContractFailure failure)
        {
            selectedFailure = SelectRequestFailure(selectedFailure, failure);
            value = default!;
            return false;
        }
    }

    private static int GetRequestFailurePrecedence(string category) => category switch
    {
        "invalid-shape" => 0,
        "invalid-vocabulary" => 1,
        "invalid-content" => 2,
        "invalid-order" => 3,
        "invalid-reference" => 4,
        _ => int.MaxValue,
    };

    private static int GetDiagnosticBlockIndex(
        DocumentationPatchDiagnostic diagnostic,
        IReadOnlyDictionary<string, int> blockIndexes) =>
        diagnostic.BlockId is not null
            && blockIndexes.TryGetValue(diagnostic.BlockId, out var index)
                ? index
                : -1;

    private static int GetDiagnosticCodePrecedence(string code) => code switch
    {
        "patch.stale.repository-context" => 0,
        "patch.stale.input-identity" => 1,
        "patch.stale.target-profile" => 2,
        "patch.stale.compilation-context" => 0,
        "patch.stale.source-encoding" => 1,
        "patch.stale.source-bytes" => 2,
        "patch.stale.source-span" => 3,
        "patch.rejected.unsupported-target" => 0,
        "patch.rejected.ambiguous-target" => 1,
        "patch.rejected.non-writable-target" => 2,
        "patch.rejected.edit-state" => 3,
        "patch.rejected.unsafe-change" => 4,
        "patch.rejected.no-effective-change" => 5,
        _ => int.MaxValue,
    };

    private static bool IsRootContextDiagnostic(string code) => code is
        "patch.stale.repository-context"
        or "patch.stale.input-identity"
        or "patch.stale.target-profile";

    private static DocumentationPatchBlockRequest ParseBlock(
        JsonElement element,
        string pointer)
    {
        ExpectProperties(
            element,
            pointer,
            "blockId",
            "symbolRef",
            "locator",
            "editKind",
            "applicableComponents",
            "content",
            "provenanceRefs");
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadOpaqueId(element, "blockId", pointer),
            ref selectedFailure,
            out string? blockId);
        TryParseRequestValue(
            () => ParseSymbolRef(element.GetProperty("symbolRef"), pointer + "/symbolRef"),
            ref selectedFailure,
            out SymbolRef? symbol);
        TryParseRequestValue(
            () => ParseLocator(element.GetProperty("locator"), pointer + "/locator"),
            ref selectedFailure,
            out DocumentationPatchSourceLocator? locator);
        TryParseRequestValue(
            () => ReadString(element, "editKind", pointer, 16) switch
            {
                "insert" => DocumentationPatchEditKind.Insert,
                "replace" => DocumentationPatchEditKind.Replace,
                _ => throw Fail("invalid-vocabulary", pointer + "/editKind"),
            },
            ref selectedFailure,
            out DocumentationPatchEditKind editKind);
        var components = ImmutableArray<DocumentationPatchApplicableComponent>.Empty;
        var componentsAreMaterializable = false;
        try
        {
            components = ParseComponents(
                element.GetProperty("applicableComponents"),
                pointer + "/applicableComponents",
                out var componentFailure,
                out componentsAreMaterializable);
            if (componentFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, componentFailure);
            }
        }
        catch (ContractFailure failure)
        {
            selectedFailure = SelectRequestFailure(selectedFailure, failure);
        }

        TryParseRequestValue(
            () => ParseContent(
                element.GetProperty("content"),
                pointer + "/content",
                components,
                validateComponentClosure: componentsAreMaterializable),
            ref selectedFailure,
            out DocumentationPatchContent? content);
        TryParseRequestValue(
            () => ParseOrderedIds(
                element.GetProperty("provenanceRefs"),
                pointer + "/provenanceRefs",
                64,
                allowEmpty: true),
            ref selectedFailure,
            out ImmutableArray<string> provenanceRefs);

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return new DocumentationPatchBlockRequest(
            blockId!,
            symbol!.Value,
            locator!,
            editKind,
            components,
            content!,
            provenanceRefs);
    }

    private static DocumentationPatchContext ParseContext(
        JsonElement element,
        string pointer)
    {
        ExpectProperties(
            element,
            pointer,
            "repositoryContextRef",
            "inputIdentity",
            "targetProfile");
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadString(element, "repositoryContextRef", pointer, 40),
            ref selectedFailure,
            out string? rawContext);
        var contextRef = default(RepositoryContextRef);
        if (rawContext is not null && !RepositoryContextRef.TryParse(rawContext, out contextRef))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/repositoryContextRef"));
        }

        TryParseRequestValue(
            () => ReadString(element, "inputIdentity", pointer, 512),
            ref selectedFailure,
            out string? inputIdentity);
        if (inputIdentity is not null && !IsCanonicalRepositoryPath(inputIdentity))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/inputIdentity"));
        }

        TryParseRequestValue(
            () => ReadString(element, "targetProfile", pointer, 32) switch
            {
                "profile.external-api" => TargetProfile.ExternalApi,
                "profile.assembly-visible" => TargetProfile.AssemblyVisible,
                _ => throw Fail("invalid-vocabulary", pointer + "/targetProfile"),
            },
            ref selectedFailure,
            out TargetProfile targetProfile);

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return new DocumentationPatchContext(contextRef, inputIdentity!, targetProfile);
    }

    private static SymbolRef ParseSymbolRef(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, "compilationContextRef", "documentationCommentId");
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadString(element, "compilationContextRef", pointer, 128),
            ref selectedFailure,
            out string? contextRef);
        if (contextRef is not null && !IsCompilationContextRef(contextRef))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/compilationContextRef"));
        }

        TryParseRequestValue(
            () => ReadString(element, "documentationCommentId", pointer, 1_024),
            ref selectedFailure,
            out string? documentationId);
        if (documentationId is not null
            && (documentationId.Length < 3
                || !"TMPFEN".Contains(documentationId[0], StringComparison.Ordinal)
                || documentationId[1] != ':'
                || ContainsControlOrInvalidScalar(documentationId)))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/documentationCommentId"));
        }

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return new SymbolRef(contextRef!, documentationId!);
    }

    private static DocumentationPatchSourceLocator ParseLocator(
        JsonElement element,
        string pointer)
    {
        ExpectObject(element, pointer);
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadString(element, "kind", pointer, 32),
            ref selectedFailure,
            out string? kind);
        if (kind is not null && kind is not ("repository" or "sourceGenerator" or "toolGenerated"))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/kind"));
        }

        var repositoryShape = string.Equals(kind, "repository", StringComparison.Ordinal)
            || kind is not ("sourceGenerator" or "toolGenerated")
                && element.TryGetProperty("path", out _);
        if (repositoryShape)
        {
            ExpectProperties(
                element,
                pointer,
                "kind",
                "path",
                "originalFileSha256",
                "encoding",
                "declarationSpan");
            TryParseRequestValue(
                () => ReadString(element, "path", pointer, 512),
                ref selectedFailure,
                out string? path);
            if (path is not null && !IsCanonicalRepositoryPath(path))
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-vocabulary", pointer + "/path"));
            }

            TryParseRequestValue(
                () => ReadString(element, "originalFileSha256", pointer, 64),
                ref selectedFailure,
                out string? digest);
            if (digest is not null && !IsSha256(digest))
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-vocabulary", pointer + "/originalFileSha256"));
            }

            TryParseRequestValue(
                () => ReadString(element, "encoding", pointer, 32) switch
                {
                    "utf-8" => DocumentationPatchRepositoryEncoding.Utf8,
                    "utf-8-bom" => DocumentationPatchRepositoryEncoding.Utf8Bom,
                    "utf-16le-bom" => DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom,
                    "utf-16be-bom" => DocumentationPatchRepositoryEncoding.Utf16BigEndianBom,
                    _ => throw Fail("invalid-vocabulary", pointer + "/encoding"),
                },
                ref selectedFailure,
                out DocumentationPatchRepositoryEncoding encoding);
            TryParseRequestValue(
                () => ParseSpan(
                    element.GetProperty("declarationSpan"),
                    pointer + "/declarationSpan"),
                ref selectedFailure,
                out Utf16Span span);

            if (selectedFailure is not null)
            {
                throw selectedFailure;
            }

            return new DocumentationPatchRepositoryLocator(path!, digest!, encoding, span);
        }

        ExpectProperties(
            element,
            pointer,
            "kind",
            "producerId",
            "outputId",
            "sourceSha256",
            "declarationSpan");
        TryParseRequestValue(
            () => ReadString(element, "producerId", pointer, 68),
            ref selectedFailure,
            out string? producerId);
        TryParseRequestValue(
            () => ReadString(element, "outputId", pointer, 68),
            ref selectedFailure,
            out string? outputId);
        TryParseRequestValue(
            () => ReadString(element, "sourceSha256", pointer, 64),
            ref selectedFailure,
            out string? digestValue);
        var generatedKind = kind is "sourceGenerator" or "toolGenerated"
            ? kind
            : "sourceGenerator";
        var expectedProducer = generatedKind == "sourceGenerator" ? "sgp." : "tgp.";
        var expectedOutput = generatedKind == "sourceGenerator" ? "sgo." : "tgo.";
        if (producerId is not null && !IsPrefixedDigest(producerId, expectedProducer))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/producerId"));
        }

        if (outputId is not null && !IsPrefixedDigest(outputId, expectedOutput))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/outputId"));
        }

        if (digestValue is not null && !IsSha256(digestValue))
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/sourceSha256"));
        }

        TryParseRequestValue(
            () => ParseSpan(
                element.GetProperty("declarationSpan"),
                pointer + "/declarationSpan"),
            ref selectedFailure,
            out Utf16Span generatedSpan);
        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return kind == "sourceGenerator"
            ? new DocumentationPatchSourceGeneratorLocator(
                producerId!,
                outputId!,
                digestValue!,
                generatedSpan)
            : new DocumentationPatchToolGeneratedLocator(
                producerId!,
                outputId!,
                digestValue!,
                generatedSpan);
    }

    private static Utf16Span ParseSpan(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, "start", "end");
        if (!element.GetProperty("start").TryGetInt32(out var start)
            || !element.GetProperty("end").TryGetInt32(out var end)
            || start < 0
            || end <= start)
        {
            throw Fail("invalid-vocabulary", pointer);
        }

        return new Utf16Span(start, end);
    }

    private static ImmutableArray<DocumentationPatchApplicableComponent> ParseComponents(
        JsonElement element,
        string pointer,
        out ContractFailure? selectedFailure,
        out bool componentsAreMaterializable)
    {
        ExpectArray(element, pointer, 0, 512);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchApplicableComponent>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var namedComponents = new HashSet<string>(StringComparer.Ordinal);
        var hasReturn = false;
        var hasValue = false;
        selectedFailure = null;
        componentsAreMaterializable = true;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            if (item.ValueKind != JsonValueKind.Object)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-shape", itemPointer));
                componentsAreMaterializable = false;
                index++;
                continue;
            }

            ContractFailure? itemFailure = null;
            TryParseRequestValue(
                () => ReadString(item, "kind", itemPointer, 16),
                ref itemFailure,
                out string? kindText);
            var hasNamedShape = item.TryGetProperty("name", out _);
            try
            {
                if (kindText is "typeParameter" or "parameter" || hasNamedShape)
                {
                    ExpectProperties(item, itemPointer, "kind", "identity", "name");
                }
                else
                {
                    ExpectProperties(item, itemPointer, "kind", "identity");
                }
            }
            catch (ContractFailure failure)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, failure);
                componentsAreMaterializable = false;
                index++;
                continue;
            }

            var kind = kindText switch
            {
                "typeParameter" => DocumentationPatchComponentKind.TypeParameter,
                "parameter" => DocumentationPatchComponentKind.Parameter,
                "return" => DocumentationPatchComponentKind.Return,
                "value" => DocumentationPatchComponentKind.Value,
                _ => (DocumentationPatchComponentKind?)null,
            };
            if (kind is null && kindText is not null)
            {
                itemFailure = SelectRequestFailure(
                    itemFailure,
                    Fail("invalid-vocabulary", itemPointer + "/kind"));
            }

            TryParseRequestValue(
                () => ReadString(item, "identity", itemPointer, 128),
                ref itemFailure,
                out string? identity);
            if (kind is not null && identity is not null && !IsComponentIdentity(kind.Value, identity))
            {
                itemFailure = SelectRequestFailure(
                    itemFailure,
                    Fail("invalid-vocabulary", itemPointer + "/identity"));
            }

            var named = kind is DocumentationPatchComponentKind.TypeParameter
                or DocumentationPatchComponentKind.Parameter
                || kind is null && hasNamedShape;
            string? name = null;
            if (named)
            {
                TryParseRequestValue(
                    () => ReadString(item, "name", itemPointer, 128),
                    ref itemFailure,
                    out name);
                if (name is not null
                    && (string.IsNullOrEmpty(name) || !TryCountXmlScalars(name, out _)))
                {
                    itemFailure = SelectRequestFailure(
                        itemFailure,
                        Fail("invalid-vocabulary", itemPointer + "/name"));
                }
            }

            if (itemFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, itemFailure);
                componentsAreMaterializable = false;
                index++;
                continue;
            }

            if (!identities.Add(identity!)
                || named && !namedComponents.Add(kindText + "\u0000" + name)
                || kind == DocumentationPatchComponentKind.Return && hasReturn
                || kind == DocumentationPatchComponentKind.Value && hasValue)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-content", itemPointer));
                componentsAreMaterializable = false;
            }

            var component = new DocumentationPatchApplicableComponent(kind!.Value, identity!, name);
            if (builder.Count > 0 && CompareComponents(builder[^1], component) >= 0)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-order", itemPointer));
            }

            hasReturn |= kind == DocumentationPatchComponentKind.Return;
            hasValue |= kind == DocumentationPatchComponentKind.Value;
            if (hasReturn && hasValue)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-content", itemPointer));
                componentsAreMaterializable = false;
            }

            builder.Add(component);
            index++;
        }

        // Keep the serialized-order failure above, but expose canonical recoverable data
        // to dependent semantic validation so ordering alone cannot manufacture content failure.
        builder.Sort(CompareComponents);
        return builder.ToImmutable();
    }

    private static DocumentationPatchContent ParseContent(
        JsonElement element,
        string pointer,
        ImmutableArray<DocumentationPatchApplicableComponent> components,
        bool validateComponentClosure)
    {
        ExpectObject(element, pointer);
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadString(element, "kind", pointer, 16),
            ref selectedFailure,
            out string? kind);
        var structuredShape = kind == "structured"
            || kind != "inheritDoc" && element.EnumerateObject().Count() > 1;
        if (!structuredShape)
        {
            ExpectProperties(element, pointer, "kind");
            if (kind != "inheritDoc")
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-vocabulary", pointer + "/kind"));
            }

            if (selectedFailure is not null)
            {
                throw selectedFailure;
            }

            return new DocumentationPatchInheritDocContent();
        }

        ExpectProperties(
            element,
            pointer,
            "kind",
            "summaryLines",
            "typeParameters",
            "parameters",
            "return",
            "value",
            "exceptions",
            "remarksLines");
        if (kind != "structured")
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-vocabulary", pointer + "/kind"));
        }

        var scalarTotal = 0;
        TryParseRequestValue(
            () => ParseLines(
                element.GetProperty("summaryLines"),
                pointer + "/summaryLines",
                ref scalarTotal),
            ref selectedFailure,
            out ImmutableArray<string> summary);
        var typeParameters = ImmutableArray<DocumentationPatchNamedContent>.Empty;
        var typeParametersAreMaterializable = false;
        try
        {
            typeParameters = ParseNamedContent(
                element.GetProperty("typeParameters"),
                pointer + "/typeParameters",
                ref scalarTotal,
                out var typeParameterFailure,
                out typeParametersAreMaterializable);
            if (typeParameterFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, typeParameterFailure);
            }
        }
        catch (ContractFailure failure)
        {
            selectedFailure = SelectRequestFailure(selectedFailure, failure);
        }

        var parameters = ImmutableArray<DocumentationPatchNamedContent>.Empty;
        var parametersAreMaterializable = false;
        try
        {
            parameters = ParseNamedContent(
                element.GetProperty("parameters"),
                pointer + "/parameters",
                ref scalarTotal,
                out var parameterFailure,
                out parametersAreMaterializable);
            if (parameterFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, parameterFailure);
            }
        }
        catch (ContractFailure failure)
        {
            selectedFailure = SelectRequestFailure(selectedFailure, failure);
        }

        var returnIsValid = TryParseRequestValue(
            () => ParseOptionalComponentContent(
                element.GetProperty("return"),
                pointer + "/return",
                ref scalarTotal),
            ref selectedFailure,
            out DocumentationPatchComponentContent? returnContent);
        var valueIsValid = TryParseRequestValue(
            () => ParseOptionalComponentContent(
                element.GetProperty("value"),
                pointer + "/value",
                ref scalarTotal),
            ref selectedFailure,
            out DocumentationPatchComponentContent? valueContent);
        if (returnContent is not null && valueContent is not null)
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-content", pointer));
        }

        TryParseRequestValue(
            () => ParseExceptions(
                element.GetProperty("exceptions"),
                pointer + "/exceptions",
                ref scalarTotal),
            ref selectedFailure,
            out ImmutableArray<DocumentationPatchExceptionContent> exceptions);
        TryParseRequestValue(
            () => element.GetProperty("remarksLines").ValueKind == JsonValueKind.Null
                ? (ImmutableArray<string>?)null
                : ParseLines(
                    element.GetProperty("remarksLines"),
                    pointer + "/remarksLines",
                    ref scalarTotal),
            ref selectedFailure,
            out ImmutableArray<string>? remarks);

        if (scalarTotal > MaximumBlockTextScalars)
        {
            selectedFailure = SelectRequestFailure(
                selectedFailure,
                Fail("invalid-content", pointer));
        }

        if (validateComponentClosure)
        {
            try
            {
                ValidateContentComponentClosure(
                    components,
                    typeParameters,
                    typeParametersAreMaterializable,
                    parameters,
                    parametersAreMaterializable,
                    returnContent,
                    returnIsValid,
                    valueContent,
                    valueIsValid,
                    pointer);
            }
            catch (ContractFailure failure)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, failure);
            }
        }

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return new DocumentationPatchStructuredContent(
            summary,
            typeParameters,
            parameters,
            returnContent,
            valueContent,
            exceptions,
            remarks);
    }

    private static ImmutableArray<DocumentationPatchNamedContent> ParseNamedContent(
        JsonElement element,
        string pointer,
        ref int scalarTotal,
        out ContractFailure? selectedFailure,
        out bool namedContentIsMaterializable)
    {
        ExpectArray(element, pointer, 0, 512);
        var accumulatedScalarTotal = scalarTotal;
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchNamedContent>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        selectedFailure = null;
        namedContentIsMaterializable = true;
        string? previous = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            try
            {
                ExpectProperties(item, itemPointer, "componentIdentity", "name", "lines");
            }
            catch (ContractFailure failure)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, failure);
                namedContentIsMaterializable = false;
                index++;
                continue;
            }

            ContractFailure? itemFailure = null;
            TryParseRequestValue(
                () => ReadString(item, "componentIdentity", itemPointer, 128),
                ref itemFailure,
                out string? identity);
            TryParseRequestValue(
                () => ReadString(item, "name", itemPointer, 128),
                ref itemFailure,
                out string? name);
            if (name is not null
                && (string.IsNullOrEmpty(name) || !TryCountXmlScalars(name, out _)))
            {
                itemFailure = SelectRequestFailure(
                    itemFailure,
                    Fail("invalid-content", itemPointer + "/name"));
            }

            TryParseRequestValue(
                () => ParseLines(
                    item.GetProperty("lines"),
                    itemPointer + "/lines",
                    ref accumulatedScalarTotal),
                ref itemFailure,
                out ImmutableArray<string> lines);
            if (identity is not null
                && previous is not null
                && string.CompareOrdinal(previous, identity) >= 0)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-order", itemPointer));
            }

            if (itemFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, itemFailure);
                namedContentIsMaterializable = false;
            }
            else
            {
                if (identities.Add(identity!))
                {
                    builder.Add(new DocumentationPatchNamedContent(identity!, name!, lines));
                }
            }

            if (identity is not null)
            {
                previous = identity;
            }

            index++;
        }

        scalarTotal = accumulatedScalarTotal;
        // As with applicable components, ordering remains a recorded artifact failure while
        // dependent closure operates on canonical recoverable identities.
        builder.Sort(static (left, right) => string.CompareOrdinal(
            left.ComponentIdentity,
            right.ComponentIdentity));
        return builder.ToImmutable();
    }

    private static DocumentationPatchComponentContent? ParseOptionalComponentContent(
        JsonElement element,
        string pointer,
        ref int scalarTotal)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectProperties(element, pointer, "componentIdentity", "lines");
        var accumulatedScalarTotal = scalarTotal;
        ContractFailure? selectedFailure = null;
        TryParseRequestValue(
            () => ReadString(element, "componentIdentity", pointer, 128),
            ref selectedFailure,
            out string? identity);
        TryParseRequestValue(
            () => ParseLines(
                element.GetProperty("lines"),
                pointer + "/lines",
                ref accumulatedScalarTotal),
            ref selectedFailure,
            out ImmutableArray<string> lines);
        if (selectedFailure is not null)
        {
            scalarTotal = accumulatedScalarTotal;
            throw selectedFailure;
        }

        scalarTotal = accumulatedScalarTotal;
        return new DocumentationPatchComponentContent(identity!, lines);
    }

    private static ImmutableArray<DocumentationPatchExceptionContent> ParseExceptions(
        JsonElement element,
        string pointer,
        ref int scalarTotal)
    {
        ExpectArray(element, pointer, 0, 256);
        var accumulatedScalarTotal = scalarTotal;
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchExceptionContent>();
        ContractFailure? selectedFailure = null;
        string? previous = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            try
            {
                ExpectProperties(item, itemPointer, "typeDocumentationId", "lines");
            }
            catch (ContractFailure failure)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, failure);
                index++;
                continue;
            }

            ContractFailure? itemFailure = null;
            TryParseRequestValue(
                () => ReadString(item, "typeDocumentationId", itemPointer, 1_024),
                ref itemFailure,
                out string? id);
            if (id is not null && !IsExceptionDocumentationId(id))
            {
                itemFailure = SelectRequestFailure(
                    itemFailure,
                    Fail("invalid-content", itemPointer + "/typeDocumentationId"));
            }

            TryParseRequestValue(
                () => ParseLines(
                    item.GetProperty("lines"),
                    itemPointer + "/lines",
                    ref accumulatedScalarTotal),
                ref itemFailure,
                out ImmutableArray<string> lines);
            if (id is not null
                && previous is not null
                && string.CompareOrdinal(previous, id) >= 0)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-order", itemPointer));
            }

            if (itemFailure is not null)
            {
                selectedFailure = SelectRequestFailure(selectedFailure, itemFailure);
            }
            else
            {
                builder.Add(new DocumentationPatchExceptionContent(id!, lines));
            }

            if (id is not null)
            {
                previous = id;
            }

            index++;
        }

        if (selectedFailure is not null)
        {
            scalarTotal = accumulatedScalarTotal;
            throw selectedFailure;
        }

        scalarTotal = accumulatedScalarTotal;
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ParseLines(
        JsonElement element,
        string pointer,
        ref int scalarTotal)
    {
        ExpectArray(element, pointer, 1, 256);
        var builder = ImmutableArray.CreateBuilder<string>();
        ContractFailure? selectedFailure = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-content", $"{pointer}/{index}"));
                index++;
                continue;
            }

            var line = item.GetString()!;
            if (line.AsSpan().IndexOfAny("\r\n\u0085\u2028\u2029") >= 0
                || !TryCountXmlScalars(line, out var count)
                || count > MaximumLogicalLineScalars)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-content", $"{pointer}/{index}"));
                index++;
                continue;
            }

            scalarTotal = checked(scalarTotal + count);
            builder.Add(line);
            index++;
        }

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return builder.ToImmutable();
    }

    private static void ValidateContentComponentClosure(
        ImmutableArray<DocumentationPatchApplicableComponent> components,
        ImmutableArray<DocumentationPatchNamedContent> typeParameters,
        bool validateTypeParameters,
        ImmutableArray<DocumentationPatchNamedContent> parameters,
        bool validateParameters,
        DocumentationPatchComponentContent? returnContent,
        bool validateReturn,
        DocumentationPatchComponentContent? valueContent,
        bool validateValue,
        string pointer)
    {
        if (validateTypeParameters)
        {
            var expectedTypeParameters = components
                .Where(component => component.Kind == DocumentationPatchComponentKind.TypeParameter)
                .ToArray();
            if (!MatchesNamed(expectedTypeParameters, typeParameters))
            {
                throw Fail("invalid-content", pointer);
            }
        }

        if (validateParameters)
        {
            var expectedParameters = components
                .Where(component => component.Kind == DocumentationPatchComponentKind.Parameter)
                .ToArray();
            if (!MatchesNamed(expectedParameters, parameters))
            {
                throw Fail("invalid-content", pointer);
            }
        }

        if (validateReturn)
        {
            var expectedReturn = components.SingleOrDefault(
                component => component.Kind == DocumentationPatchComponentKind.Return);
            if ((expectedReturn is null) != (returnContent is null)
                || expectedReturn is not null
                    && !string.Equals(
                        expectedReturn.Identity,
                        returnContent!.ComponentIdentity,
                        StringComparison.Ordinal))
            {
                throw Fail("invalid-content", pointer);
            }
        }

        if (validateValue)
        {
            var expectedValue = components.SingleOrDefault(
                component => component.Kind == DocumentationPatchComponentKind.Value);
            if ((expectedValue is null) != (valueContent is null)
                || expectedValue is not null
                    && !string.Equals(
                        expectedValue.Identity,
                        valueContent!.ComponentIdentity,
                        StringComparison.Ordinal))
            {
                throw Fail("invalid-content", pointer);
            }
        }
    }

    private static bool MatchesNamed(
        IReadOnlyList<DocumentationPatchApplicableComponent> expected,
        ImmutableArray<DocumentationPatchNamedContent> actual)
    {
        if (expected.Count != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(expected[index].Identity, actual[index].ComponentIdentity, StringComparison.Ordinal)
                || !string.Equals(expected[index].Name, actual[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<DocumentationPatchTargetTrace> ParseTargets(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 1, 512);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchTargetTrace>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(
                item,
                itemPointer,
                "blockId",
                "symbolRef",
                "locator",
                "provenanceRefs",
                "status");
            var status = ReadString(item, "status", itemPointer, 16) switch
            {
                "valid" => DocumentationPatchTargetStatus.Valid,
                "invalid" => DocumentationPatchTargetStatus.Invalid,
                "stale" => DocumentationPatchTargetStatus.Stale,
                "not-evaluated" => DocumentationPatchTargetStatus.NotEvaluated,
                _ => throw Fail("invalid-vocabulary", itemPointer + "/status"),
            };
            builder.Add(new DocumentationPatchTargetTrace(
                ReadOpaqueId(item, "blockId", itemPointer),
                ParseSymbolRef(item.GetProperty("symbolRef"), itemPointer + "/symbolRef"),
                ParseLocator(item.GetProperty("locator"), itemPointer + "/locator"),
                ParseOrderedIds(
                    item.GetProperty("provenanceRefs"),
                    itemPointer + "/provenanceRefs",
                    64,
                    allowEmpty: true),
                status));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationPatchChangedFile> ParseChangedFiles(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 0, 512);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchChangedFile>();
        string? previousPath = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(
                item,
                itemPointer,
                "path",
                "originalFileSha256",
                "candidateFileSha256",
                "changedDocumentationBlockCount",
                "originalDocumentationByteCount",
                "candidateDocumentationByteCount",
                "originalDocumentationLineCount",
                "candidateDocumentationLineCount");
            var path = ReadString(item, "path", itemPointer, 512);
            var originalDigest = ReadString(item, "originalFileSha256", itemPointer, 64);
            var candidateDigest = ReadString(item, "candidateFileSha256", itemPointer, 64);
            if (!IsCanonicalRepositoryPath(path)
                || !IsSha256(originalDigest)
                || !IsSha256(candidateDigest)
                || previousPath is not null && string.CompareOrdinal(previousPath, path) >= 0)
            {
                throw Fail("invalid-order", itemPointer);
            }

            builder.Add(new DocumentationPatchChangedFile(
                path,
                originalDigest,
                candidateDigest,
                ReadCount(item, "changedDocumentationBlockCount", itemPointer),
                ReadCount(item, "originalDocumentationByteCount", itemPointer),
                ReadCount(item, "candidateDocumentationByteCount", itemPointer),
                ReadCount(item, "originalDocumentationLineCount", itemPointer),
                ReadCount(item, "candidateDocumentationLineCount", itemPointer)));
            previousPath = path;
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationPatchInvariantResult> ParseInvariants(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, InvariantIds.Length, InvariantIds.Length);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchInvariantResult>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, "id", "status");
            var id = ReadString(item, "id", itemPointer, 128);
            if (!string.Equals(id, InvariantIds[index], StringComparison.Ordinal))
            {
                throw Fail("invalid-order", itemPointer + "/id");
            }

            var status = ReadString(item, "status", itemPointer, 16) switch
            {
                "passed" => DocumentationPatchInvariantStatus.Passed,
                "failed" => DocumentationPatchInvariantStatus.Failed,
                "not-run" => DocumentationPatchInvariantStatus.NotRun,
                _ => throw Fail("invalid-vocabulary", itemPointer + "/status"),
            };
            builder.Add(new DocumentationPatchInvariantResult(id, status));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationPatchDiagnostic> ParseDiagnostics(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 0, 128);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchDiagnostic>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, "severity", "code", "blockId", "path", "pointer");
            var severity = ReadString(item, "severity", itemPointer, 16) switch
            {
                "error" => DocumentationPatchDiagnosticSeverity.Error,
                _ => throw Fail("invalid-vocabulary", itemPointer + "/severity"),
            };
            var code = ReadString(item, "code", itemPointer, 128);
            if (!ResultDiagnosticCodes.Contains(code))
            {
                throw Fail("invalid-vocabulary", itemPointer + "/code");
            }

            var blockId = ReadOptionalString(item, "blockId", itemPointer, 128);
            var path = ReadOptionalString(item, "path", itemPointer, 512);
            var jsonPointer = ReadOptionalString(item, "pointer", itemPointer, 512);
            if (blockId is not null && !IsOpaqueId(blockId, 128)
                || path is not null && !IsCanonicalRepositoryPath(path)
                || jsonPointer is not null && !IsJsonPointer(jsonPointer))
            {
                throw Fail("invalid-vocabulary", itemPointer);
            }

            builder.Add(new DocumentationPatchDiagnostic(
                severity,
                code,
                blockId,
                path,
                jsonPointer));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ParseOrderedIds(
        JsonElement element,
        string pointer,
        int maximum,
        bool allowEmpty)
    {
        ExpectArray(element, pointer, allowEmpty ? 0 : 1, maximum);
        var builder = ImmutableArray.CreateBuilder<string>();
        ContractFailure? selectedFailure = null;
        string? previous = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-shape", $"{pointer}/{index}"));
                index++;
                continue;
            }

            var id = item.GetString()!;
            if (!IsOpaqueId(id, 128))
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-vocabulary", $"{pointer}/{index}"));
                index++;
                continue;
            }

            if (previous is not null && string.CompareOrdinal(previous, id) >= 0)
            {
                selectedFailure = SelectRequestFailure(
                    selectedFailure,
                    Fail("invalid-order", $"{pointer}/{index}"));
            }

            builder.Add(id);
            previous = id;
            index++;
        }

        if (selectedFailure is not null)
        {
            throw selectedFailure;
        }

        return builder.ToImmutable();
    }

    private static PatchRequestValidationFailure? TryParseArtifact(
        ReadOnlyMemory<byte> utf8Json,
        string prefix,
        string versionProperty,
        out JsonDocument? document)
    {
        document = null;
        if (utf8Json.Length > MaximumArtifactUtf8Bytes)
        {
            return new PatchRequestValidationFailure(prefix + ".document-too-large", null);
        }

        if (HasPrefix(utf8Json.Span, 0xef, 0xbb, 0xbf)
            || HasPrefix(utf8Json.Span, 0xff, 0xfe)
            || HasPrefix(utf8Json.Span, 0xfe, 0xff))
        {
            return new PatchRequestValidationFailure(prefix + ".bom-not-allowed", null);
        }

        try
        {
            _ = StrictUtf8.GetString(utf8Json.Span);
        }
        catch (DecoderFallbackException)
        {
            return new PatchRequestValidationFailure(prefix + ".invalid-utf8", null);
        }

        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
        }
        catch (JsonException)
        {
            return new PatchRequestValidationFailure(prefix + ".invalid-json", null);
        }

        var duplicatePointer = FindDuplicateProperty(document.RootElement, string.Empty);
        if (duplicatePointer is not null)
        {
            document.Dispose();
            document = null;
            return new PatchRequestValidationFailure(prefix + ".duplicate-property", duplicatePointer);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null;
            return new PatchRequestValidationFailure(prefix + ".invalid-shape", string.Empty);
        }

        if (!document.RootElement.TryGetProperty(versionProperty, out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber))
        {
            document.Dispose();
            document = null;
            return new PatchRequestValidationFailure(prefix + ".invalid-shape", "/" + versionProperty);
        }

        if (versionNumber != 1)
        {
            document.Dispose();
            document = null;
            return new PatchRequestValidationFailure(prefix + ".unsupported-version", "/" + versionProperty);
        }

        return null;
    }

    private static PatchResultValidationFailure? TryParseResultArtifact(
        ReadOnlyMemory<byte> utf8Json,
        string versionProperty,
        out JsonDocument? document)
    {
        var requestFailure = TryParseArtifact(
            utf8Json,
            "patch.result",
            versionProperty,
            out document);
        return requestFailure is null
            ? null
            : new PatchResultValidationFailure(requestFailure.Code, requestFailure.Pointer);
    }

    private static string? FindDuplicateProperty(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPointer = pointer + "/" + EscapePointer(property.Name);
                if (!names.Add(property.Name))
                {
                    return propertyPointer;
                }

                var nested = FindDuplicateProperty(property.Value, propertyPointer);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindDuplicateProperty(item, pointer + "/" + index);
                if (nested is not null)
                {
                    return nested;
                }

                index++;
            }
        }

        return null;
    }

    private static void ExpectProperties(
        JsonElement element,
        string pointer,
        params string[] properties)
    {
        ExpectObject(element, pointer);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != properties.Length
            || actual.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
        {
            throw Fail("invalid-shape", pointer);
        }
    }

    private static void ExpectObject(JsonElement element, string pointer)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Fail("invalid-shape", pointer);
        }
    }

    private static void ExpectArray(
        JsonElement element,
        string pointer,
        int minimum,
        int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() < minimum
            || element.GetArrayLength() > maximum)
        {
            throw Fail("invalid-shape", pointer);
        }
    }

    private static string ReadString(
        JsonElement parent,
        string propertyName,
        string pointer,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            throw Fail("invalid-shape", pointer + "/" + propertyName);
        }

        var value = element.GetString()!;
        if (!TryCountScalars(value, out var scalarCount) || scalarCount > maximumLength)
        {
            throw Fail("invalid-vocabulary", pointer + "/" + propertyName);
        }

        return value;
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName,
        string pointer,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            throw Fail("invalid-shape", pointer + "/" + propertyName);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when TryCountScalars(element.GetString()!, out var scalarCount)
                && scalarCount <= maximumLength => element.GetString(),
            _ => throw Fail("invalid-vocabulary", pointer + "/" + propertyName),
        };
    }

    private static string ReadOpaqueId(
        JsonElement parent,
        string propertyName,
        string pointer)
    {
        var value = ReadString(parent, propertyName, pointer, 128);
        if (!IsOpaqueId(value, 128))
        {
            throw Fail("invalid-vocabulary", pointer + "/" + propertyName);
        }

        return value;
    }

    private static int ReadCount(JsonElement parent, string propertyName, string pointer)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < 0)
        {
            throw Fail("invalid-vocabulary", pointer + "/" + propertyName);
        }

        return value;
    }

    private static DocumentationPatchOutcome ParseOutcome(string value) => value switch
    {
        "accepted" => DocumentationPatchOutcome.Accepted,
        "rejected" => DocumentationPatchOutcome.Rejected,
        "stale" => DocumentationPatchOutcome.Stale,
        _ => throw Fail("invalid-vocabulary", "/outcome"),
    };

    private static int CompareBlocks(
        DocumentationPatchBlockRequest left,
        DocumentationPatchBlockRequest right)
    {
        var comparison = left.Locator.Kind.CompareTo(right.Locator.Kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(GetLocatorPrimary(left.Locator), GetLocatorPrimary(right.Locator));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(GetLocatorSecondary(left.Locator), GetLocatorSecondary(right.Locator));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Locator.DeclarationSpan.Start.CompareTo(right.Locator.DeclarationSpan.Start);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Locator.DeclarationSpan.End.CompareTo(right.Locator.DeclarationSpan.End);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(
            left.SymbolRef.CompilationContextRef,
            right.SymbolRef.CompilationContextRef);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.CompareOrdinal(
            left.SymbolRef.DocumentationCommentId,
            right.SymbolRef.DocumentationCommentId);
        return comparison != 0
            ? comparison
            : string.CompareOrdinal(left.BlockId, right.BlockId);
    }

    private static int CompareComponents(
        DocumentationPatchApplicableComponent left,
        DocumentationPatchApplicableComponent right)
    {
        var comparison = left.Kind.CompareTo(right.Kind);
        return comparison != 0
            ? comparison
            : string.CompareOrdinal(left.Identity, right.Identity);
    }

    private static string GetLocatorPrimary(DocumentationPatchSourceLocator locator) => locator switch
    {
        DocumentationPatchRepositoryLocator repository => repository.Path,
        DocumentationPatchGeneratedLocator generated => generated.ProducerId,
        _ => throw new InvalidOperationException("Unknown documentation patch locator."),
    };

    private static string GetLocatorSecondary(DocumentationPatchSourceLocator locator) => locator switch
    {
        DocumentationPatchRepositoryLocator => string.Empty,
        DocumentationPatchGeneratedLocator generated => generated.OutputId,
        _ => throw new InvalidOperationException("Unknown documentation patch locator."),
    };

    private static string GetLocatorBindingKey(DocumentationPatchSourceLocator locator) =>
        ((int)locator.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "\u0000"
        + GetLocatorPrimary(locator)
        + "\u0000"
        + GetLocatorSecondary(locator)
        + "\u0000"
        + locator.DeclarationSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "\u0000"
        + locator.DeclarationSpan.End.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsCanonicalRepositoryPath(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value[0] == '/'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal)
            || value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return TryCountScalars(value, out var scalarCount) && scalarCount <= 512;
    }

    private static bool IsOpaqueId(string value, int maximum)
    {
        if (value.Length is 0 || value.Length > maximum)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCompilationContextRef(string value)
    {
        if (value.Length is 0 or > 128 || !IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsLowerAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsComponentIdentity(
        DocumentationPatchComponentKind kind,
        string identity) => kind switch
        {
            DocumentationPatchComponentKind.TypeParameter =>
                IsCanonicalOrdinalIdentity(identity, "type-parameter/"),
            DocumentationPatchComponentKind.Parameter =>
                IsCanonicalOrdinalIdentity(identity, "parameter/"),
            DocumentationPatchComponentKind.Return => identity == "return",
            DocumentationPatchComponentKind.Value => identity == "value",
            _ => false,
        };

    private static bool IsCanonicalOrdinalIdentity(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var ordinal = value.AsSpan(prefix.Length);
        return ordinal.Length > 0
            && ordinal.IndexOfAnyExceptInRange('0', '9') < 0
            && (ordinal.Length == 1 || ordinal[0] != '0');
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(IsLowerHex);

    private static bool IsPrefixedDigest(string value, string prefix) =>
        value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).ToArray().All(IsLowerHex);

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsExceptionDocumentationId(string value)
    {
        if (value.Length < 3
            || !value.StartsWith("T:", StringComparison.Ordinal)
            || !TryCountXmlScalars(value, out _))
        {
            return false;
        }

        foreach (var rune in value.AsSpan(2).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)
                || Rune.IsControl(rune)
                || rune.Value is '<' or '>' or '&' or '"' or '\'')
            {
                return false;
            }
        }

        return TryCountScalars(value, out _);
    }

    private static bool ContainsControlOrInvalidScalar(string value)
    {
        if (!TryCountScalars(value, out _))
        {
            return true;
        }

        return value.EnumerateRunes().Any(Rune.IsControl);
    }

    private static bool TryCountXmlScalars(string value, out int count)
    {
        if (!TryCountScalars(value, out count))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (scalar != 0x09
                && scalar is not (>= 0x20 and <= 0xd7ff)
                && scalar is not (>= 0xe000 and <= 0xfffd)
                && scalar is not (>= 0x10000 and <= 0x10ffff))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCountScalars(string value, out int count)
    {
        count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }

            count++;
        }

        return true;
    }

    private static bool IsValidSpan(Utf16Span span, int textLength) =>
        span.Start >= 0 && span.Start < span.End && span.End <= textLength;

    private static bool HasAnyBom(ReadOnlySpan<byte> bytes) =>
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

    private static bool IsJsonPointer(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] != '/')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '~')
            {
                continue;
            }

            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapePointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static string MapResultCategory(string category) => category switch
    {
        "invalid-content" or "invalid-reference" => "invalid-correlation",
        _ => category,
    };

    private static DocumentationPatchValidationCheck Valid(string? decodedText = null) =>
        new(true, null, DecodedText: decodedText);

    private static DocumentationPatchValidationCheck Invalid(
        string code,
        string? blockId = null) =>
        new(false, code, blockId);

    private static ContractFailure Fail(string category, string? pointer) =>
        new(category, pointer);

    private sealed class ContractFailure(string category, string? pointer) : Exception
    {
        public string Category { get; } = category;

        public string? Pointer { get; } = pointer;
    }
}
