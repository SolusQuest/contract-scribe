using System.Collections.Immutable;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

[Flags]
public enum DocumentationScribeRepositoryToolOperations
{
    None = 0,
    ReadExcerpt = 1,
    ListFiles = 2,
    SearchText = 4,
}

public static class DocumentationScribeRepositoryToolOperationIds
{
    public const string ReadExcerpt = "repository.read-excerpt";
    public const string ListFiles = "repository.list-files";
    public const string SearchText = "repository.search-text";
}

public static class DocumentationScribeRepositoryToolFailureCodes
{
    public const string InvalidRequest = "repository.invalid-request";
    public const string ScopeDenied = "repository.scope-denied";
    public const string UnsafeObject = "repository.unsafe-object";
    public const string Stale = "repository.stale";
    public const string InvalidEncoding = "repository.invalid-encoding";
    public const string InvalidCursor = "repository.invalid-cursor";
    public const string Unavailable = "repository.unavailable";
    public const string Budget = "repository.budget-exhausted";
    public const string Timeout = "repository.timed-out";
    public const string Cancelled = "repository.cancelled";
}

public sealed record DocumentationScribeRepositoryToolLimits
{
    private DocumentationScribeRepositoryToolLimits(
        int maximumEntriesPerCall,
        int maximumFilesPerCall,
        int maximumFileUtf8Bytes,
        int maximumBytesReadPerRun,
        int maximumReturnedUtf8BytesPerRun,
        int maximumReturnedItemsPerRun,
        int maximumMatchesPerCall,
        int maximumDirectoryDepth,
        int maximumDirectoriesPerRun,
        int maximumRouteDepth,
        int maximumPageSize,
        int maximumActiveChains,
        int maximumCallsPerOperation,
        int maximumElapsedMilliseconds)
    {
        MaximumEntriesPerCall = maximumEntriesPerCall;
        MaximumFilesPerCall = maximumFilesPerCall;
        MaximumFileUtf8Bytes = maximumFileUtf8Bytes;
        MaximumBytesReadPerRun = maximumBytesReadPerRun;
        MaximumReturnedUtf8BytesPerRun = maximumReturnedUtf8BytesPerRun;
        MaximumReturnedItemsPerRun = maximumReturnedItemsPerRun;
        MaximumMatchesPerCall = maximumMatchesPerCall;
        MaximumDirectoryDepth = maximumDirectoryDepth;
        MaximumDirectoriesPerRun = maximumDirectoriesPerRun;
        MaximumRouteDepth = maximumRouteDepth;
        MaximumPageSize = maximumPageSize;
        MaximumActiveChains = maximumActiveChains;
        MaximumCallsPerOperation = maximumCallsPerOperation;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
    }

    public int MaximumEntriesPerCall { get; }
    public int MaximumFilesPerCall { get; }
    public int MaximumFileUtf8Bytes { get; }
    public int MaximumBytesReadPerRun { get; }
    public int MaximumReturnedUtf8BytesPerRun { get; }
    public int MaximumReturnedItemsPerRun { get; }
    public int MaximumMatchesPerCall { get; }
    public int MaximumDirectoryDepth { get; }
    public int MaximumDirectoriesPerRun { get; }
    public int MaximumRouteDepth { get; }
    public int MaximumPageSize { get; }
    public int MaximumActiveChains { get; }
    public int MaximumCallsPerOperation { get; }
    public int MaximumElapsedMilliseconds { get; }

    public static DocumentationScribeRepositoryToolLimits Create(
        int maximumEntriesPerCall = 1_024,
        int maximumFilesPerCall = 256,
        int maximumFileUtf8Bytes = 262_144,
        int maximumBytesReadPerRun = 1_048_576,
        int maximumReturnedUtf8BytesPerRun = 262_144,
        int maximumReturnedItemsPerRun = 512,
        int maximumMatchesPerCall = 256,
        int maximumDirectoryDepth = 32,
        int maximumDirectoriesPerRun = 1_024,
        int maximumRouteDepth = 32,
        int maximumPageSize = 64,
        int maximumActiveChains = 32,
        int maximumCallsPerOperation = 64,
        int maximumElapsedMilliseconds = 30_000)
    {
        var values = new[]
        {
            maximumEntriesPerCall, maximumFilesPerCall, maximumFileUtf8Bytes,
            maximumBytesReadPerRun, maximumReturnedUtf8BytesPerRun,
            maximumReturnedItemsPerRun, maximumMatchesPerCall, maximumPageSize,
            maximumDirectoryDepth, maximumDirectoriesPerRun, maximumRouteDepth,
            maximumActiveChains, maximumCallsPerOperation, maximumElapsedMilliseconds,
        };
        if (values.Any(value => value <= 0)
            || maximumPageSize > 4_096
            || maximumFileUtf8Bytes > DocumentationScribeContract.MaximumArtifactUtf8Bytes
            || maximumReturnedUtf8BytesPerRun > DocumentationScribeContract.MaximumArtifactUtf8Bytes
            || maximumElapsedMilliseconds > DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntriesPerCall));
        }

        return new(
            maximumEntriesPerCall, maximumFilesPerCall, maximumFileUtf8Bytes,
            maximumBytesReadPerRun, maximumReturnedUtf8BytesPerRun,
            maximumReturnedItemsPerRun, maximumMatchesPerCall,
            maximumDirectoryDepth, maximumDirectoriesPerRun, maximumRouteDepth,
            maximumPageSize,
            maximumActiveChains, maximumCallsPerOperation, maximumElapsedMilliseconds);
    }
}

public sealed record DocumentationScribeRepositoryToolScope
{
    private DocumentationScribeRepositoryToolScope(
        string scopeId,
        string repositoryPath,
        bool isDirectory,
        DocumentationScribeRepositoryToolOperations operations,
        bool required,
        bool recursive,
        ImmutableArray<string> extensions,
        DocumentationScribeContextRole role,
        EvidenceSubject? subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        DocumentationScribeEvidenceAuthority authority,
        ImmutableArray<string> claimCategoryIds)
    {
        ScopeId = scopeId;
        RepositoryPath = repositoryPath;
        IsDirectory = isDirectory;
        Operations = operations;
        Required = required;
        Recursive = recursive;
        Extensions = extensions;
        Role = role;
        Subject = subject;
        Kind = kind;
        Relation = relation;
        Authority = authority;
        ClaimCategoryIds = claimCategoryIds;
    }

    public string ScopeId { get; }
    public string RepositoryPath { get; internal init; }
    public bool IsDirectory { get; }
    public DocumentationScribeRepositoryToolOperations Operations { get; }
    public bool Required { get; }
    public bool Recursive { get; }
    public ImmutableArray<string> Extensions { get; }
    public DocumentationScribeContextRole Role { get; }
    public EvidenceSubject? Subject { get; }
    public EvidenceKind Kind { get; }
    public EvidenceRelation Relation { get; }
    public DocumentationScribeEvidenceAuthority Authority { get; }
    public ImmutableArray<string> ClaimCategoryIds { get; }

    public static DocumentationScribeRepositoryToolScope Directory(
        string scopeId,
        string repositoryPath,
        DocumentationScribeRepositoryToolOperations operations,
        DocumentationScribeContextRole role,
        bool required = true,
        bool recursive = true,
        IEnumerable<string>? extensions = null,
        EvidenceSubject? subject = null,
        EvidenceKind kind = EvidenceKind.RepositoryDocumentation,
        EvidenceRelation relation = EvidenceRelation.Documents,
        DocumentationScribeEvidenceAuthority authority = DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
        IEnumerable<string>? claimCategoryIds = null) =>
        Create(scopeId, repositoryPath, true, operations, role, required, recursive,
            extensions, subject, kind, relation, authority, claimCategoryIds);

    public static DocumentationScribeRepositoryToolScope File(
        string scopeId,
        string repositoryPath,
        DocumentationScribeRepositoryToolOperations operations,
        DocumentationScribeContextRole role,
        bool required = true,
        EvidenceSubject? subject = null,
        EvidenceKind kind = EvidenceKind.RepositoryDocumentation,
        EvidenceRelation relation = EvidenceRelation.Documents,
        DocumentationScribeEvidenceAuthority authority = DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
        IEnumerable<string>? claimCategoryIds = null) =>
        Create(scopeId, repositoryPath, false, operations, role, required, false,
            null, subject, kind, relation, authority, claimCategoryIds);

    private static DocumentationScribeRepositoryToolScope Create(
        string scopeId,
        string repositoryPath,
        bool isDirectory,
        DocumentationScribeRepositoryToolOperations operations,
        DocumentationScribeContextRole role,
        bool required,
        bool recursive,
        IEnumerable<string>? extensions,
        EvidenceSubject? subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        DocumentationScribeEvidenceAuthority authority,
        IEnumerable<string>? claimCategoryIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(repositoryPath);
        var normalizedExtensions = (extensions ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .ToImmutableArray();
        var claims = (claimCategoryIds ?? []).ToImmutableArray();
        if (operations == DocumentationScribeRepositoryToolOperations.None
            || (operations & ~(
                DocumentationScribeRepositoryToolOperations.ReadExcerpt
                | DocumentationScribeRepositoryToolOperations.ListFiles
                | DocumentationScribeRepositoryToolOperations.SearchText)) != 0
            || normalizedExtensions.Any(value => value.Length < 2 || value[0] != '.' || value.Contains('/') || value.Contains('\\'))
            || claims.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The repository scope is outside the closed tool boundary.");
        }

        return new(
            scopeId, repositoryPath, isDirectory, operations, required, recursive,
            normalizedExtensions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            role, subject, kind, relation, authority,
            claims.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
    }
}

public sealed record DocumentationScribeRepositoryReadExcerptRequest(
    string ScopeId,
    string? RepositoryPath = null,
    int? StartLine = null,
    int? EndLine = null) : IDocumentationScribeToolRequest<DocumentationScribeRepositoryReadExcerptResult>;

public sealed record DocumentationScribeRepositoryListFilesRequest(
    string ScopeId,
    string? Subdirectory = null,
    int PageSize = 32,
    string? Cursor = null) : IDocumentationScribeToolRequest<DocumentationScribeRepositoryListFilesResult>;

public sealed record DocumentationScribeRepositorySearchTextRequest(
    string ScopeId,
    string Literal,
    string? Subdirectory = null,
    int PageSize = 32,
    string? Cursor = null) : IDocumentationScribeToolRequest<DocumentationScribeRepositorySearchTextResult>;

public sealed record DocumentationScribeRepositoryExcerpt(
    string RepositoryPath,
    string Content,
    int StartUtf16,
    int EndUtf16,
    string ContentSha256,
    int OriginalUtf8ByteCount,
    int IncludedUtf8ByteCount,
    bool IsTruncated,
    int? MatchStartUtf16 = null,
    int? MatchEndUtf16 = null)
{
    public override string ToString() =>
        $"{nameof(DocumentationScribeRepositoryExcerpt)} {{ RepositoryPath = {RepositoryPath}, Content = <authorized-content>, IsTruncated = {IsTruncated} }}";
}

public sealed record DocumentationScribeRepositoryFileItem(
    string RepositoryPath,
    string ContentSha256,
    int Utf8ByteCount)
{
    public override string ToString() =>
        $"{nameof(DocumentationScribeRepositoryFileItem)} {{ RepositoryPath = {RepositoryPath}, Utf8ByteCount = {Utf8ByteCount} }}";
}

public sealed record DocumentationScribeRepositoryReadExcerptResult(
    DocumentationScribeToolOutcome Outcome,
    string? FailureCode,
    DocumentationScribeRepositoryExcerpt? Excerpt,
    DocumentationScribeInstructionRouteFact? Route,
    ImmutableArray<DocumentationScribeDynamicEvidenceInput> DynamicEvidence) : IDocumentationScribeToolResult
{
    public override string ToString() =>
        $"{nameof(DocumentationScribeRepositoryReadExcerptResult)} {{ Outcome = {Outcome.Id}, FailureCode = {FailureCode ?? "none"}, HasContent = {Excerpt is not null} }}";
}

public sealed record DocumentationScribeRepositoryListFilesResult(
    DocumentationScribeToolOutcome Outcome,
    string? FailureCode,
    ImmutableArray<DocumentationScribeRepositoryFileItem> Items,
    string? Cursor) : IDocumentationScribeToolResult
{
    public override string ToString() =>
        $"{nameof(DocumentationScribeRepositoryListFilesResult)} {{ Outcome = {Outcome.Id}, FailureCode = {FailureCode ?? "none"}, Items = {Items.Length}, HasCursor = {Cursor is not null} }}";
}

public sealed record DocumentationScribeRepositorySearchTextResult(
    DocumentationScribeToolOutcome Outcome,
    string? FailureCode,
    ImmutableArray<DocumentationScribeRepositoryExcerpt> Items,
    string? Cursor,
    ImmutableArray<DocumentationScribeInstructionRouteFact> Routes,
    ImmutableArray<DocumentationScribeDynamicEvidenceInput> DynamicEvidence) : IDocumentationScribeToolResult
{
    public override string ToString() =>
        $"{nameof(DocumentationScribeRepositorySearchTextResult)} {{ Outcome = {Outcome.Id}, FailureCode = {FailureCode ?? "none"}, Items = {Items.Length}, HasCursor = {Cursor is not null} }}";
}

public sealed class DocumentationScribeRepositoryToolDescriptor<TRequest, TResult>
    : IDocumentationScribeToolDescriptor<TRequest, TResult>
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    internal DocumentationScribeRepositoryToolDescriptor(string operationId) => OperationId = operationId;
    public string OperationId { get; }
}

public static class DocumentationScribeRepositoryToolSchemas
{
    public const string ReadExcerptDescription = "Read one bounded UTF-8 excerpt inside an authorized repository scope.";
    public const string ListFilesDescription = "List bounded repository-relative regular files inside an authorized scope.";
    public const string SearchTextDescription = "Search for one ordinal literal inside an authorized repository scope.";

    public static ReadOnlyMemory<byte> ReadExcerptInputUtf8Json { get; } = Encoding.UTF8.GetBytes(
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"scopeId\"],\"properties\":{\"scopeId\":{\"type\":\"string\"},\"repositoryPath\":{\"type\":\"string\"},\"startLine\":{\"type\":\"integer\",\"minimum\":1},\"endLine\":{\"type\":\"integer\",\"minimum\":1}}}");

    public static ReadOnlyMemory<byte> ListFilesInputUtf8Json { get; } = Encoding.UTF8.GetBytes(
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"scopeId\"],\"properties\":{\"scopeId\":{\"type\":\"string\"},\"subdirectory\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\",\"minimum\":1},\"cursor\":{\"type\":\"string\"}}}");

    public static ReadOnlyMemory<byte> SearchTextInputUtf8Json { get; } = Encoding.UTF8.GetBytes(
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"scopeId\",\"literal\"],\"properties\":{\"scopeId\":{\"type\":\"string\"},\"literal\":{\"type\":\"string\",\"minLength\":1},\"subdirectory\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\",\"minimum\":1},\"cursor\":{\"type\":\"string\"}}}");
}

public sealed class DocumentationScribeRepositoryToolBundle
{
    private DocumentationScribeRepositoryToolBundle(DocumentationScribeRepositoryToolSession session)
    {
        ReadExcerpt = new DocumentationScribeRepositoryReadExcerptTool(session);
        ListFiles = new DocumentationScribeRepositoryListFilesTool(session);
        SearchText = new DocumentationScribeRepositorySearchTextTool(session);
    }

    public static DocumentationScribeRepositoryToolDescriptor<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult> ReadExcerptDescriptor { get; } = new(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt);
    public static DocumentationScribeRepositoryToolDescriptor<DocumentationScribeRepositoryListFilesRequest, DocumentationScribeRepositoryListFilesResult> ListFilesDescriptor { get; } = new(DocumentationScribeRepositoryToolOperationIds.ListFiles);
    public static DocumentationScribeRepositoryToolDescriptor<DocumentationScribeRepositorySearchTextRequest, DocumentationScribeRepositorySearchTextResult> SearchTextDescriptor { get; } = new(DocumentationScribeRepositoryToolOperationIds.SearchText);

    public IDocumentationScribeToolPort<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult> ReadExcerpt { get; }
    public IDocumentationScribeToolPort<DocumentationScribeRepositoryListFilesRequest, DocumentationScribeRepositoryListFilesResult> ListFiles { get; }
    public IDocumentationScribeToolPort<DocumentationScribeRepositorySearchTextRequest, DocumentationScribeRepositorySearchTextResult> SearchText { get; }

    public static DocumentationScribeRepositoryToolBundle Create(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeLoadedContext loadedContext,
        IEnumerable<DocumentationScribeRepositoryToolScope> scopes,
        DocumentationScribeRepositoryToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loadedContext);
        ArgumentNullException.ThrowIfNull(scopes);
        return new(new(request, attemptId, loadedContext, scopes, limits ?? DocumentationScribeRepositoryToolLimits.Create()));
    }
}
