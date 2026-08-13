using System.Collections.Immutable;

namespace ContractScribe.Core;

public readonly record struct RepositoryContextRef
{
    private RepositoryContextRef(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryParse(string? value, out RepositoryContextRef result)
    {
        if (value is { Length: 40 }
            && value.StartsWith("repoctx-", StringComparison.Ordinal)
            && value.AsSpan(8).IndexOfAnyExcept("0123456789abcdef") < 0)
        {
            result = new RepositoryContextRef(value);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}

public enum DocumentationPatchSourceKind
{
    Repository,
    SourceGenerator,
    ToolGenerated,
}

public enum DocumentationPatchEditKind
{
    Insert,
    Replace,
}

public enum DocumentationPatchComponentKind
{
    TypeParameter,
    Parameter,
    Return,
    Value,
}

public enum DocumentationPatchRepositoryEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndianBom,
    Utf16BigEndianBom,
}

public enum DocumentationPatchOutcome
{
    Accepted,
    Rejected,
    Stale,
}

public enum DocumentationPatchTargetStatus
{
    Valid,
    Invalid,
    Stale,
    NotEvaluated,
}

public enum DocumentationPatchInvariantStatus
{
    Passed,
    Failed,
    NotRun,
}

public enum DocumentationPatchDiagnosticSeverity
{
    Error,
}

public sealed record DocumentationPatchContext
{
    internal DocumentationPatchContext(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile)
    {
        RepositoryContextRef = repositoryContextRef;
        InputIdentity = inputIdentity;
        TargetProfile = targetProfile;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string InputIdentity { get; }

    public TargetProfile TargetProfile { get; }
}

public abstract record DocumentationPatchSourceLocator
{
    private protected DocumentationPatchSourceLocator(
        DocumentationPatchSourceKind kind,
        Utf16Span declarationSpan)
    {
        Kind = kind;
        DeclarationSpan = declarationSpan;
    }

    public DocumentationPatchSourceKind Kind { get; }

    public Utf16Span DeclarationSpan { get; }
}

public sealed record DocumentationPatchRepositoryLocator : DocumentationPatchSourceLocator
{
    internal DocumentationPatchRepositoryLocator(
        string path,
        string originalFileSha256,
        DocumentationPatchRepositoryEncoding encoding,
        Utf16Span declarationSpan)
        : base(DocumentationPatchSourceKind.Repository, declarationSpan)
    {
        Path = path;
        OriginalFileSha256 = originalFileSha256;
        Encoding = encoding;
    }

    public string Path { get; }

    public string OriginalFileSha256 { get; }

    public DocumentationPatchRepositoryEncoding Encoding { get; }
}

public abstract record DocumentationPatchGeneratedLocator : DocumentationPatchSourceLocator
{
    private protected DocumentationPatchGeneratedLocator(
        DocumentationPatchSourceKind kind,
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span declarationSpan)
        : base(kind, declarationSpan)
    {
        ProducerId = producerId;
        OutputId = outputId;
        SourceSha256 = sourceSha256;
    }

    public string ProducerId { get; }

    public string OutputId { get; }

    public string SourceSha256 { get; }
}

public sealed record DocumentationPatchSourceGeneratorLocator : DocumentationPatchGeneratedLocator
{
    internal DocumentationPatchSourceGeneratorLocator(
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span declarationSpan)
        : base(
            DocumentationPatchSourceKind.SourceGenerator,
            producerId,
            outputId,
            sourceSha256,
            declarationSpan)
    {
    }
}

public sealed record DocumentationPatchToolGeneratedLocator : DocumentationPatchGeneratedLocator
{
    internal DocumentationPatchToolGeneratedLocator(
        string producerId,
        string outputId,
        string sourceSha256,
        Utf16Span declarationSpan)
        : base(
            DocumentationPatchSourceKind.ToolGenerated,
            producerId,
            outputId,
            sourceSha256,
            declarationSpan)
    {
    }
}

public sealed record DocumentationPatchApplicableComponent
{
    internal DocumentationPatchApplicableComponent(
        DocumentationPatchComponentKind kind,
        string identity,
        string? name)
    {
        Kind = kind;
        Identity = identity;
        Name = name;
    }

    public DocumentationPatchComponentKind Kind { get; }

    public string Identity { get; }

    public string? Name { get; }
}

public abstract record DocumentationPatchContent
{
    private protected DocumentationPatchContent()
    {
    }
}

public sealed record DocumentationPatchInheritDocContent : DocumentationPatchContent
{
    internal DocumentationPatchInheritDocContent()
    {
    }
}

public sealed record DocumentationPatchNamedContent
{
    internal DocumentationPatchNamedContent(
        string componentIdentity,
        string name,
        ImmutableArray<string> lines)
    {
        ComponentIdentity = componentIdentity;
        Name = name;
        Lines = lines;
    }

    public string ComponentIdentity { get; }

    public string Name { get; }

    public ImmutableArray<string> Lines { get; }
}

public sealed record DocumentationPatchComponentContent
{
    internal DocumentationPatchComponentContent(
        string componentIdentity,
        ImmutableArray<string> lines)
    {
        ComponentIdentity = componentIdentity;
        Lines = lines;
    }

    public string ComponentIdentity { get; }

    public ImmutableArray<string> Lines { get; }
}

public sealed record DocumentationPatchExceptionContent
{
    internal DocumentationPatchExceptionContent(
        string typeDocumentationId,
        ImmutableArray<string> lines)
    {
        TypeDocumentationId = typeDocumentationId;
        Lines = lines;
    }

    public string TypeDocumentationId { get; }

    public ImmutableArray<string> Lines { get; }
}

public sealed record DocumentationPatchStructuredContent : DocumentationPatchContent
{
    internal DocumentationPatchStructuredContent(
        ImmutableArray<string> summaryLines,
        ImmutableArray<DocumentationPatchNamedContent> typeParameters,
        ImmutableArray<DocumentationPatchNamedContent> parameters,
        DocumentationPatchComponentContent? returnContent,
        DocumentationPatchComponentContent? valueContent,
        ImmutableArray<DocumentationPatchExceptionContent> exceptions,
        ImmutableArray<string>? remarksLines)
    {
        SummaryLines = summaryLines;
        TypeParameters = typeParameters;
        Parameters = parameters;
        Return = returnContent;
        Value = valueContent;
        Exceptions = exceptions;
        RemarksLines = remarksLines;
    }

    public ImmutableArray<string> SummaryLines { get; }

    public ImmutableArray<DocumentationPatchNamedContent> TypeParameters { get; }

    public ImmutableArray<DocumentationPatchNamedContent> Parameters { get; }

    public DocumentationPatchComponentContent? Return { get; }

    public DocumentationPatchComponentContent? Value { get; }

    public ImmutableArray<DocumentationPatchExceptionContent> Exceptions { get; }

    public ImmutableArray<string>? RemarksLines { get; }
}

public sealed record DocumentationPatchBlockRequest
{
    internal DocumentationPatchBlockRequest(
        string blockId,
        SymbolRef symbolRef,
        DocumentationPatchSourceLocator locator,
        DocumentationPatchEditKind editKind,
        ImmutableArray<DocumentationPatchApplicableComponent> applicableComponents,
        DocumentationPatchContent content,
        ImmutableArray<string> provenanceRefs)
    {
        BlockId = blockId;
        SymbolRef = symbolRef;
        Locator = locator;
        EditKind = editKind;
        ApplicableComponents = applicableComponents;
        Content = content;
        ProvenanceRefs = provenanceRefs;
    }

    public string BlockId { get; }

    public SymbolRef SymbolRef { get; }

    public DocumentationPatchSourceLocator Locator { get; }

    public DocumentationPatchEditKind EditKind { get; }

    public ImmutableArray<DocumentationPatchApplicableComponent> ApplicableComponents { get; }

    public DocumentationPatchContent Content { get; }

    public ImmutableArray<string> ProvenanceRefs { get; }
}

public sealed record DocumentationPatchRequest
{
    internal DocumentationPatchRequest(
        string artifactSha256,
        DocumentationPatchContext context,
        ImmutableArray<string> provenanceCatalog,
        ImmutableArray<DocumentationPatchBlockRequest> blocks)
    {
        ArtifactSha256 = artifactSha256;
        Context = context;
        ProvenanceCatalog = provenanceCatalog;
        Blocks = blocks;
    }

    public int PatchRequestVersion => 1;

    public string ArtifactSha256 { get; }

    public DocumentationPatchContext Context { get; }

    public ImmutableArray<string> ProvenanceCatalog { get; }

    public ImmutableArray<DocumentationPatchBlockRequest> Blocks { get; }
}

public sealed record PatchRequestValidationFailure(string Code, string? Pointer);

public sealed record PatchResultValidationFailure(string Code, string? Pointer);

public sealed class DocumentationPatchRequestParseResult
{
    internal DocumentationPatchRequestParseResult(
        DocumentationPatchRequest? request,
        PatchRequestValidationFailure? failure)
    {
        Request = request;
        Failure = failure;
    }

    public bool IsValid => Request is not null;

    public DocumentationPatchRequest? Request { get; }

    public PatchRequestValidationFailure? Failure { get; }
}

public sealed class DocumentationPatchValidationContext
{
    public DocumentationPatchValidationContext(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        IEnumerable<string> compilationContextRefs)
    {
        if (string.IsNullOrEmpty(inputIdentity))
        {
            throw new ArgumentException("The input identity is required.", nameof(inputIdentity));
        }

        RepositoryContextRef = repositoryContextRef;
        InputIdentity = inputIdentity;
        TargetProfile = targetProfile;
        CompilationContextRefs = compilationContextRefs?.ToImmutableHashSet(StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(compilationContextRefs));
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string InputIdentity { get; }

    public TargetProfile TargetProfile { get; }

    public ImmutableHashSet<string> CompilationContextRefs { get; }
}

public sealed record DocumentationPatchValidationCheck(
    bool IsValid,
    string? Code,
    string? BlockId = null,
    string? DecodedText = null);

public sealed record DocumentationPatchTargetTrace
{
    internal DocumentationPatchTargetTrace(
        string blockId,
        SymbolRef symbolRef,
        DocumentationPatchSourceLocator locator,
        ImmutableArray<string> provenanceRefs,
        DocumentationPatchTargetStatus status)
    {
        BlockId = blockId;
        SymbolRef = symbolRef;
        Locator = locator;
        ProvenanceRefs = provenanceRefs;
        Status = status;
    }

    public string BlockId { get; }

    public SymbolRef SymbolRef { get; }

    public DocumentationPatchSourceLocator Locator { get; }

    public ImmutableArray<string> ProvenanceRefs { get; }

    public DocumentationPatchTargetStatus Status { get; }
}

public sealed record DocumentationPatchChangedFile
{
    internal DocumentationPatchChangedFile(
        string path,
        string originalFileSha256,
        string candidateFileSha256,
        int changedDocumentationBlockCount,
        int originalDocumentationByteCount,
        int candidateDocumentationByteCount,
        int originalDocumentationLineCount,
        int candidateDocumentationLineCount)
    {
        Path = path;
        OriginalFileSha256 = originalFileSha256;
        CandidateFileSha256 = candidateFileSha256;
        ChangedDocumentationBlockCount = changedDocumentationBlockCount;
        OriginalDocumentationByteCount = originalDocumentationByteCount;
        CandidateDocumentationByteCount = candidateDocumentationByteCount;
        OriginalDocumentationLineCount = originalDocumentationLineCount;
        CandidateDocumentationLineCount = candidateDocumentationLineCount;
    }

    public string Path { get; }

    public string OriginalFileSha256 { get; }

    public string CandidateFileSha256 { get; }

    public int ChangedDocumentationBlockCount { get; }

    public int OriginalDocumentationByteCount { get; }

    public int CandidateDocumentationByteCount { get; }

    public int OriginalDocumentationLineCount { get; }

    public int CandidateDocumentationLineCount { get; }
}

public sealed record DocumentationPatchInvariantResult(
    string Id,
    DocumentationPatchInvariantStatus Status);

public sealed record DocumentationPatchDiagnostic(
    DocumentationPatchDiagnosticSeverity Severity,
    string Code,
    string? BlockId,
    string? Path,
    string? Pointer);

public sealed record DocumentationPatchValidationResult
{
    internal DocumentationPatchValidationResult(
        string patchRequestSha256,
        DocumentationPatchContext context,
        DocumentationPatchOutcome outcome,
        ImmutableArray<DocumentationPatchTargetTrace> targets,
        ImmutableArray<DocumentationPatchChangedFile> changedFiles,
        int changedDocumentationBlockCount,
        ImmutableArray<DocumentationPatchInvariantResult> invariants,
        ImmutableArray<DocumentationPatchDiagnostic> diagnostics)
    {
        PatchRequestSha256 = patchRequestSha256;
        Context = context;
        Outcome = outcome;
        Targets = targets;
        ChangedFiles = changedFiles;
        ChangedDocumentationBlockCount = changedDocumentationBlockCount;
        Invariants = invariants;
        Diagnostics = diagnostics;
    }

    public int PatchValidationResultVersion => 1;

    public string PatchRequestSha256 { get; }

    public DocumentationPatchContext Context { get; }

    public DocumentationPatchOutcome Outcome { get; }

    public ImmutableArray<DocumentationPatchTargetTrace> Targets { get; }

    public ImmutableArray<DocumentationPatchChangedFile> ChangedFiles { get; }

    public int ChangedDocumentationBlockCount { get; }

    public ImmutableArray<DocumentationPatchInvariantResult> Invariants { get; }

    public ImmutableArray<DocumentationPatchDiagnostic> Diagnostics { get; }
}

public sealed class DocumentationPatchResultParseResult
{
    internal DocumentationPatchResultParseResult(
        DocumentationPatchValidationResult? result,
        PatchResultValidationFailure? failure)
    {
        Result = result;
        Failure = failure;
    }

    public bool IsValid => Result is not null;

    public DocumentationPatchValidationResult? Result { get; }

    public PatchResultValidationFailure? Failure { get; }
}
