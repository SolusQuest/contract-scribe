using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal sealed class DocumentationScribeRepositoryToolSession
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly DocumentationScribeRequest request;
    private readonly DocumentationScribeAttemptId attemptId;
    private readonly DocumentationScribeLoadedContext loadedContext;
    private readonly DocumentationScribeRepositoryToolLimits limits;
    private readonly ImmutableDictionary<string, BoundScope> scopes;
    private readonly string runCorrelation = Guid.NewGuid().ToString("N");
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<(ulong Volume, ulong FileId), string> physicalPaths = [];
    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageChain> cursorChains = new(StringComparer.Ordinal);
    private readonly HashSet<string> consumedCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);
    private long bytesRead;
    private long returnedBytes;
    private int returnedItems;
    private int directoriesInspected;
    private int activeChains;

    internal DocumentationScribeRepositoryToolSession(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeLoadedContext loadedContext,
        IEnumerable<DocumentationScribeRepositoryToolScope> scopes,
        DocumentationScribeRepositoryToolLimits limits)
    {
        this.request = request;
        this.attemptId = attemptId;
        this.loadedContext = loadedContext;
        this.limits = limits;
        if (!DocumentationScribeAttemptId.TryParse(attemptId.Value, out _))
        {
            throw new ArgumentException("A valid attempt identifier is required.", nameof(attemptId));
        }

        var binding = loadedContext.ValidateRequestBinding(request);
        if (!binding.IsValid)
        {
            throw new ArgumentException("The repository context is not bound to the request.", nameof(loadedContext));
        }

        var builder = ImmutableDictionary.CreateBuilder<string, BoundScope>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var bound = BindScope(scope);
            if (!builder.TryAdd(scope.ScopeId, bound))
            {
                throw new ArgumentException("Repository scope identifiers must be unique.", nameof(scopes));
            }
        }

        if (builder.Count == 0)
        {
            throw new ArgumentException("At least one repository scope is required.", nameof(scopes));
        }

        this.scopes = builder.ToImmutable();
    }

    internal DocumentationScribeRepositoryReadExcerptResult ReadExcerpt(
        DocumentationScribeRepositoryReadExcerptRequest toolRequest,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            try
            {
                if (toolRequest is null)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
                }

                BeginCall(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, cancellationToken);
                var scope = GetScope(toolRequest.ScopeId, DocumentationScribeRepositoryToolOperations.ReadExcerpt);
                var path = ResolveFile(scope, toolRequest.RepositoryPath);
                var file = ReadFile(path, scope.Scope.Required, cancellationToken);
                var range = SelectLines(file.Text, toolRequest.StartLine, toolRequest.EndLine);
                var excerpt = CreateExcerpt(file, range.Start, range.End);
                var route = CreateRoute(scope, file, cancellationToken);
                var evidence = CreateEvidence(scope, file, range.Start, range.End, range.IsWholeFile);
                CommitPublication(excerpt.IncludedUtf8ByteCount, 1, cancellationToken);
                return new(
                    range.IsWholeFile ? DocumentationScribeToolOutcome.Complete : DocumentationScribeToolOutcome.Incomplete,
                    null,
                    excerpt,
                    route,
                    evidence is null ? [] : [evidence]);
            }
            catch (Exception exception) when (TryFailure(exception, out var outcome, out var code))
            {
                return new(outcome, code, null, null, []);
            }
        }
    }

    internal DocumentationScribeRepositoryListFilesResult ListFiles(
        DocumentationScribeRepositoryListFilesRequest toolRequest,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            try
            {
                if (toolRequest is null)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
                }

                BeginCall(DocumentationScribeRepositoryToolOperationIds.ListFiles, cancellationToken);
                var scope = GetScope(toolRequest.ScopeId, DocumentationScribeRepositoryToolOperations.ListFiles);
                var query = NormalizeQuery(scope, toolRequest.Subdirectory, null, toolRequest.PageSize);
                var page = Page(
                    DocumentationScribeRepositoryToolOperationIds.ListFiles,
                    scope,
                    query,
                    toolRequest.Cursor,
                    cancellationToken,
                    () => ListInventory(scope, query.Path, cancellationToken)
                        .Select(file => new PageValue(
                            string.Join('\0', file.RepositoryPath, file.ContentSha256, file.Utf8ByteCount),
                            file,
                            null))
                        .ToImmutableArray());
                var items = page.Values.Select(value => (DocumentationScribeRepositoryFileItem)value.Value).ToImmutableArray();
                return new(
                    page.HasMore ? DocumentationScribeToolOutcome.Incomplete : DocumentationScribeToolOutcome.Complete,
                    null,
                    items,
                    page.Cursor);
            }
            catch (Exception exception) when (TryFailure(exception, out var outcome, out var code))
            {
                return new(outcome, code, [], null);
            }
        }
    }

    internal DocumentationScribeRepositorySearchTextResult SearchText(
        DocumentationScribeRepositorySearchTextRequest toolRequest,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            try
            {
                if (toolRequest is null)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
                }

                BeginCall(DocumentationScribeRepositoryToolOperationIds.SearchText, cancellationToken);
                var scope = GetScope(toolRequest.ScopeId, DocumentationScribeRepositoryToolOperations.SearchText);
                ValidateLiteral(toolRequest.Literal);
                var query = NormalizeQuery(scope, toolRequest.Subdirectory, toolRequest.Literal, toolRequest.PageSize);
                var routes = ImmutableArray.CreateBuilder<DocumentationScribeInstructionRouteFact>();
                var evidence = ImmutableArray.CreateBuilder<DocumentationScribeDynamicEvidenceInput>();
                var page = Page(
                    DocumentationScribeRepositoryToolOperationIds.SearchText,
                    scope,
                    query,
                    toolRequest.Cursor,
                    cancellationToken,
                    () => Search(scope, query.Path, toolRequest.Literal, cancellationToken),
                    values => PrepareSearchPublication(
                        scope, values, routes, evidence, cancellationToken));
                var items = page.Values.Select(value => (DocumentationScribeRepositoryExcerpt)value.Value).ToImmutableArray();

                return new(
                    page.HasMore ? DocumentationScribeToolOutcome.Incomplete : DocumentationScribeToolOutcome.Complete,
                    null,
                    items,
                    page.Cursor,
                    routes.ToImmutable(),
                    evidence.ToImmutable());
            }
            catch (Exception exception) when (TryFailure(exception, out var outcome, out var code))
            {
                return new(outcome, code, [], null, [], []);
            }
        }
    }

    private void PrepareSearchPublication(
        BoundScope scope,
        ImmutableArray<PageValue> values,
        ImmutableArray<DocumentationScribeInstructionRouteFact>.Builder routes,
        ImmutableArray<DocumentationScribeDynamicEvidenceInput>.Builder evidence,
        CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            var file = (StableTextFile)value.Source!;
            var item = (DocumentationScribeRepositoryExcerpt)value.Value;
            if (CreateRoute(scope, file, cancellationToken) is { } route)
            {
                routes.Add(route);
            }

            if (CreateEvidence(scope, file, item.StartUtf16, item.EndUtf16, false) is { } row)
            {
                evidence.Add(row);
            }
        }
    }

    private BoundScope BindScope(DocumentationScribeRepositoryToolScope scope)
    {
        var path = NormalizeRepositoryPath(scope.RepositoryPath, allowEmpty: scope.IsDirectory);
        var contextMatches = request.ContextReferences.Where(item =>
            string.Equals(item.ContextReferenceId, scope.ScopeId, StringComparison.Ordinal)).ToArray();
        var evidenceMatches = request.EvidenceReferences.Where(item =>
            string.Equals(item.EvidenceReferenceId, scope.ScopeId, StringComparison.Ordinal)).ToArray();
        if (contextMatches.Length + evidenceMatches.Length != 1)
        {
            throw new ArgumentException("Every repository scope must bind one request-visible reference.", nameof(scope));
        }

        DocumentationScribeInstructionContextFact? origin = null;
        string anchorPath;
        if (contextMatches.Length == 1)
        {
            var reference = contextMatches[0];
            anchorPath = NormalizeRepositoryPath(reference.Path, allowEmpty: false);
            if (reference.RepositoryContextRef != request.Context.RepositoryContextRef)
            {
                throw new ArgumentException("The scope reference belongs to another repository context.", nameof(scope));
            }

            if (reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction)
            {
                var matches = loadedContext.Facts.Instructions.Where(item =>
                    item.Commitment.RepositoryPath == reference.Path
                    && item.Commitment.ContentSha256 == reference.ContentSha256
                    && item.Commitment.OriginalUtf8ByteCount == reference.OriginalUtf8ByteCount
                    && item.Commitment.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
                    && item.Commitment.IsTruncated == reference.IsTruncated).ToArray();
                if (matches.Length != 1)
                {
                    throw new ArgumentException("The instruction scope is not uniquely commitment-bound.", nameof(scope));
                }

                origin = matches[0];
            }
        }
        else
        {
            var reference = evidenceMatches[0];
            if (reference.RepositoryContextRef != request.Context.RepositoryContextRef
                || reference.Locator is not RepositoryEvidenceLocator locator)
            {
                throw new ArgumentException("The evidence scope is not repository-bound.", nameof(scope));
            }

            anchorPath = NormalizeRepositoryPath(locator.Path, allowEmpty: false);
        }

        if (scope.IsDirectory)
        {
            if (!IsWithin(path, anchorPath))
            {
                throw new ArgumentException("The scope root does not contain its visible anchor.", nameof(scope));
            }
        }
        else if (!string.Equals(path, anchorPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("An exact-file scope must equal its visible anchor path.", nameof(scope));
        }

        return new(scope with { RepositoryPath = path }, origin);
    }

    private BoundScope GetScope(string scopeId, DocumentationScribeRepositoryToolOperations operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        if (!loadedContext.ValidateRequestBinding(request).IsValid || !loadedContext.IsCurrent)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
        }

        if (!scopes.TryGetValue(scopeId, out var scope) || !scope.Scope.Operations.HasFlag(operation))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.ScopeDenied);
        }

        return scope;
    }

    private void BeginCall(string operationId, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        calls.TryGetValue(operationId, out var count);
        count = checked(count + 1);
        calls[operationId] = count;
        if (count > limits.MaximumCallsPerOperation)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }
    }

    private string ResolveFile(BoundScope scope, string? candidate)
    {
        if (!scope.Scope.IsDirectory)
        {
            if (candidate is not null
                && !string.Equals(
                    NormalizeRepositoryPath(candidate, allowEmpty: false),
                    scope.Scope.RepositoryPath,
                    StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.ScopeDenied);
            }

            return scope.Scope.RepositoryPath;
        }

        if (candidate is null)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        var path = NormalizeRepositoryPath(candidate, allowEmpty: false);
        if (!IsWithin(scope.Scope.RepositoryPath, path))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.ScopeDenied);
        }

        return path;
    }

    private NormalizedQuery NormalizeQuery(
        BoundScope scope,
        string? subdirectory,
        string? literal,
        int pageSize)
    {
        if (!scope.Scope.IsDirectory && subdirectory is not null)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        if (pageSize <= 0 || pageSize > limits.MaximumPageSize)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }

        var path = scope.Scope.RepositoryPath;
        if (scope.Scope.IsDirectory && subdirectory is not null)
        {
            path = NormalizeRepositoryPath(subdirectory, allowEmpty: true);
            if (!IsWithin(scope.Scope.RepositoryPath, path))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.ScopeDenied);
            }
        }

        return new(path, literal, pageSize);
    }

    private ImmutableArray<DocumentationScribeRepositoryFileItem> ListInventory(
        BoundScope scope,
        string path,
        CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(scope, path, cancellationToken);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeRepositoryFileItem>(files.Length);
        foreach (var repositoryPath in files)
        {
            var file = ReadFile(repositoryPath, scope.Scope.Required, cancellationToken);
            builder.Add(new(repositoryPath, file.ContentSha256, file.RawBytes.Length));
        }

        return builder.ToImmutable();
    }

    private ImmutableArray<PageValue> Search(
        BoundScope scope,
        string path,
        string literal,
        CancellationToken cancellationToken)
    {
        var paths = scope.Scope.IsDirectory
            ? EnumerateFiles(scope, path, cancellationToken)
            : ImmutableArray.Create(scope.Scope.RepositoryPath);
        var builder = ImmutableArray.CreateBuilder<PageValue>();
        foreach (var repositoryPath in paths)
        {
            var file = ReadFile(repositoryPath, scope.Scope.Required, cancellationToken);
            var offset = 0;
            while (offset <= file.Text.Length - literal.Length)
            {
                Check(cancellationToken);
                var match = file.Text.IndexOf(literal, offset, StringComparison.Ordinal);
                if (match < 0)
                {
                    break;
                }

                var end = checked(match + literal.Length);
                if (builder.Count >= limits.MaximumMatchesPerCall)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
                }

                var lineStart = LineStart(file.Text, match);
                var lineEnd = LineEnd(file.Text, end);
                var excerpt = CreateExcerpt(file, lineStart, lineEnd, match, end);
                builder.Add(new(
                    string.Join('\0', repositoryPath, match.ToString("D10", System.Globalization.CultureInfo.InvariantCulture), file.ContentSha256, Sha256(StrictUtf8.GetBytes(excerpt.Content))),
                    excerpt,
                    file));
                offset = end;
            }
        }

        return builder.OrderBy(value => value.Key, StringComparer.Ordinal).ToImmutableArray();
    }

    private ImmutableArray<string> EnumerateFiles(
        BoundScope scope,
        string path,
        CancellationToken cancellationToken)
    {
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var start = FullPath(root, path);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((start, 0));
        var files = new List<string>();
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        while (pending.Count > 0)
        {
            Check(cancellationToken);
            var (directory, depth) = pending.Pop();
            if (depth > limits.MaximumDirectoryDepth)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
            }

            directoriesInspected = checked(directoriesInspected + 1);
            if (directoriesInspected > limits.MaximumDirectoriesPerRun)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
            }

            try
            {
                _ = DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(directory);
            }
            catch (DocumentationScribeContextReadException exception) when (
                !scope.Scope.Required
                && exception.Failure == DocumentationScribeContextReadFailure.Stale)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (DirectoryNotFoundException)
            {
                throw Failure(scope.Scope.Required
                    ? DocumentationScribeRepositoryToolFailureCodes.Stale
                    : DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }

            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                Check(cancellationToken);
                inspected = checked(inspected + 1);
                if (inspected > limits.MaximumEntriesPerCall)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (scope.Scope.Recursive)
                    {
                        pending.Push((entry, checked(depth + 1)));
                    }

                    continue;
                }

                var repositoryPath = RepositoryPath(root, entry);
                if (scope.Scope.Extensions.Length != 0
                    && !scope.Scope.Extensions.Contains(Path.GetExtension(repositoryPath), StringComparer.Ordinal))
                {
                    continue;
                }

                if (spellings.TryGetValue(repositoryPath, out var existing)
                    && !string.Equals(existing, repositoryPath, StringComparison.Ordinal))
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
                }

                spellings[repositoryPath] = repositoryPath;
                files.Add(repositoryPath);
                if (files.Count > limits.MaximumFilesPerCall)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files.ToImmutableArray();
    }

    private StableTextFile ReadFile(
        string repositoryPath,
        bool required,
        CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var fullPath = FullPath(root, repositoryPath);
        var parent = Path.GetDirectoryName(fullPath)!;
        var before = DocumentationScribeContextDirectoryChain.Read(root, parent);
        DocumentationScribeContextStableRead read;
        try
        {
            read = DocumentationScribeContextStableFileReader.ReadRegularFile(
                fullPath,
                limits.MaximumFileUtf8Bytes,
                cancellationToken,
                () => Check(cancellationToken));
        }
        catch (DocumentationScribeContextReadException exception)
        {
            var previouslyAccepted = observations.ContainsKey(repositoryPath);
            throw Failure(exception.Failure switch
            {
                DocumentationScribeContextReadFailure.Budget => DocumentationScribeRepositoryToolFailureCodes.Budget,
                DocumentationScribeContextReadFailure.Unsafe => DocumentationScribeRepositoryToolFailureCodes.UnsafeObject,
                _ when !required && !previouslyAccepted => DocumentationScribeRepositoryToolFailureCodes.Unavailable,
                _ => DocumentationScribeRepositoryToolFailureCodes.Stale,
            });
        }

        ChargeBytes(read.Bytes.Length);
        var after = DocumentationScribeContextDirectoryChain.Read(root, parent);
        if (!before.SequenceEqual(after) || read.Identity.LinkCount != 1)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
        }

        var identity = (read.Identity.Volume, read.Identity.FileId);
        if (physicalPaths.TryGetValue(identity, out var existing)
            && !string.Equals(existing, repositoryPath, StringComparison.Ordinal))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
        }

        physicalPaths[identity] = repositoryPath;
        string text;
        try
        {
            var hasBom = HasBom(read.Bytes);
            text = StrictUtf8.GetString(hasBom ? read.Bytes.AsSpan(3) : read.Bytes);
            if (text.IndexOf('\0') >= 0)
            {
                throw new DecoderFallbackException();
            }
        }
        catch (DecoderFallbackException)
        {
            throw Failure(required
                ? DocumentationScribeRepositoryToolFailureCodes.InvalidEncoding
                : DocumentationScribeRepositoryToolFailureCodes.Unavailable);
        }

        var sha = Sha256(read.Bytes);
        if (observations.TryGetValue(repositoryPath, out var accepted)
            && (accepted.Identity != read.Identity
                || !accepted.DirectoryChain.SequenceEqual(after)
                || !string.Equals(accepted.ContentSha256, sha, StringComparison.Ordinal)))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
        }

        observations[repositoryPath] = new(fullPath, repositoryPath, read.Identity, after, sha);
        return new(repositoryPath, read.Bytes, text, sha, HasBom(read.Bytes));
    }

    private PageResult Page(
        string operationId,
        BoundScope scope,
        NormalizedQuery query,
        string? cursorValue,
        CancellationToken cancellationToken,
        Func<ImmutableArray<PageValue>> materialize,
        Action<ImmutableArray<PageValue>>? preparePublication = null)
    {
        PageChain? chain = null;
        var reserved = false;
        var position = 0;
        DocumentationScribeContextCursor? current = null;
        if (cursorValue is null)
        {
            if (activeChains >= limits.MaximumActiveChains)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
            }

            activeChains++;
            reserved = true;
        }
        else
        {
            if (consumedCursors.Contains(cursorValue)
                || !cursorChains.TryGetValue(cursorValue, out chain)
                || !DocumentationScribeContextCursor.TryParse(cursorValue, out var parsed)
                || chain.OperationId != operationId
                || chain.ScopeId != scope.Scope.ScopeId
                || chain.Query != query)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }

            current = parsed;
            if (!loadedContext.TryValidateCursor(parsed, chain.CursorScope, out position, cancellationToken))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }
        }

        try
        {
            var values = materialize();
            var fingerprint = Sha256(Encoding.UTF8.GetBytes(string.Join('\n', values.Select(value => value.Key))));
            if (chain is not null && !string.Equals(chain.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            if (position > values.Length)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }

            chain ??= CreateChain(operationId, scope, query, fingerprint);
            var count = Math.Min(query.PageSize, values.Length - position);
            var page = values.Skip(position).Take(count).ToImmutableArray();
            var hasMore = position + count < values.Length;
            preparePublication?.Invoke(page);
            VerifyFresh(cancellationToken);
            var publicationBytes = page.Sum(value => value.Value is DocumentationScribeRepositoryExcerpt excerpt
                ? excerpt.IncludedUtf8ByteCount
                : 0);
            var nextReturnedBytes = checked(returnedBytes + publicationBytes);
            var nextReturnedItems = checked(returnedItems + page.Length);
            if (nextReturnedBytes > limits.MaximumReturnedUtf8BytesPerRun
                || nextReturnedItems > limits.MaximumReturnedItemsPerRun)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
            }

            var next = loadedContext.IssueCursor(chain.CursorScope, current, count, hasMore, cancellationToken);
            if (next is { } candidate
                && (cursorChains.ContainsKey(candidate.Value)
                    || consumedCursors.Contains(candidate.Value)))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }

            returnedBytes = nextReturnedBytes;
            returnedItems = nextReturnedItems;
            if (cursorValue is not null)
            {
                cursorChains.Remove(cursorValue);
                consumedCursors.Add(cursorValue);
            }

            if (next is { } issued)
            {
                cursorChains.Add(issued.Value, chain);
            }
            else
            {
                activeChains--;
            }

            reserved = false;
            return new(page, next?.Value, hasMore);
        }
        finally
        {
            if (reserved)
            {
                activeChains--;
            }
        }
    }

    private PageChain CreateChain(string operationId, BoundScope scope, NormalizedQuery query, string fingerprint)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var normalizedSha = Sha256(Encoding.UTF8.GetBytes(string.Join('\0',
            request.ArtifactSha256,
            attemptId.Value,
            runCorrelation,
            correlation,
            operationId,
            scope.Scope.ScopeId,
            query.Path,
            query.Literal ?? string.Empty,
            query.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fingerprint)));
        var commitments = DocumentationScribeContextValidation.ComputeCommitmentsSha256(
            loadedContext.Facts.Instructions.Select(item => item.Commitment)
                .Concat(loadedContext.Facts.Evidence.Select(item => item.Commitment)));
        var cursorScope = DocumentationScribeContextValidation.CreateCursorScope(
            operationId,
            normalizedSha,
            request.Context.RepositoryContextRef,
            request.Target.SymbolRef,
            "repository.ordinal-v1",
            query.PageSize,
            commitments);
        return new(operationId, scope.Scope.ScopeId, query, fingerprint, cursorScope);
    }

    private DocumentationScribeRepositoryExcerpt CreateExcerpt(
        StableTextFile file,
        int start,
        int end,
        int? matchStart = null,
        int? matchEnd = null)
    {
        if (start < 0 || end < start || end > file.Text.Length
            || SplitsSurrogate(file.Text, start) || SplitsSurrogate(file.Text, end))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        var content = file.Text[start..end];
        var included = StrictUtf8.GetByteCount(content);
        return new(
            file.RepositoryPath,
            content,
            start,
            end,
            file.ContentSha256,
            file.RawBytes.Length,
            included,
            start != 0 || end != file.Text.Length,
            matchStart,
            matchEnd);
    }

    private DocumentationScribeDynamicEvidenceInput? CreateEvidence(
        BoundScope scope,
        StableTextFile file,
        int start,
        int end,
        bool wholeFile)
    {
        if (scope.Scope.Subject is null)
        {
            return null;
        }

        var content = file.Text[start..end];
        var included = wholeFile && file.HasBom ? file.RawBytes.Length : StrictUtf8.GetByteCount(content);
        var locator = wholeFile
            ? EvidenceInput.RepositoryLocator(file.RepositoryPath)
            : EvidenceInput.RepositoryLocator(file.RepositoryPath, start, end);
        var input = new DocumentationScribeDynamicEvidenceInput(
            scope.Scope.Subject,
            scope.Scope.Kind,
            scope.Scope.Relation,
            scope.Scope.Authority,
            locator,
            file.ContentSha256,
            file.RawBytes.Length,
            included,
            included < file.RawBytes.Length,
            scope.Scope.ClaimCategoryIds);
        return DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, input, out _)
            ? input
            : null;
    }

    private DocumentationScribeInstructionRouteFact? CreateRoute(
        BoundScope scope,
        StableTextFile file,
        CancellationToken cancellationToken)
    {
        if (scope.Origin is null)
        {
            return null;
        }

        Check(cancellationToken);
        var depth = checked(scope.Origin.Depth + 1);
        if (depth > limits.MaximumRouteDepth)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }

        var includedSha = file.ContentSha256;
        var commitment = DocumentationScribeContextValidation.CreateSourceCommitment(
            file.RepositoryPath,
            file.ContentSha256,
            includedSha,
            file.RawBytes.Length,
            file.RawBytes.Length,
            false,
            file.HasBom,
            file.HasBom);
        return DocumentationScribeContextValidation.CreateInstructionRoute(
            scope.Origin.InstructionId,
            file.RepositoryPath,
            scope.Scope.Role,
            DocumentationScribeContextRouteSelection.ScribeSelected,
            depth,
            commitment);
    }

    private void CommitPublication(int utf8Bytes, int items, CancellationToken cancellationToken)
    {
        VerifyFresh(cancellationToken);
        var nextBytes = checked(returnedBytes + utf8Bytes);
        var nextItems = checked(returnedItems + items);
        if (nextBytes > limits.MaximumReturnedUtf8BytesPerRun
            || nextItems > limits.MaximumReturnedItemsPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }

        cancellationToken.ThrowIfCancellationRequested();
        returnedBytes = nextBytes;
        returnedItems = nextItems;
    }

    private void VerifyFresh(CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        if (!loadedContext.VerifyFreshness(cancellationToken))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
        }

        foreach (var observation in observations.Values)
        {
            Check(cancellationToken);
            var directories = DocumentationScribeContextDirectoryChain.Read(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                Path.GetDirectoryName(observation.FullPath)!);
            var read = DocumentationScribeContextStableFileReader.ReadRegularFile(
                observation.FullPath,
                limits.MaximumFileUtf8Bytes,
                cancellationToken,
                () => Check(cancellationToken));
            ChargeBytes(read.Bytes.Length);
            if (read.Identity != observation.Identity
                || !directories.SequenceEqual(observation.DirectoryChain)
                || !string.Equals(Sha256(read.Bytes), observation.ContentSha256, StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }
        }
    }

    private void ChargeBytes(int value)
    {
        bytesRead = checked(bytesRead + value);
        if (bytesRead > limits.MaximumBytesReadPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }
    }

    private void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > limits.MaximumElapsedMilliseconds)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Timeout);
        }
    }

    private static (int Start, int End, bool IsWholeFile) SelectLines(
        string text,
        int? startLine,
        int? endLine)
    {
        if (startLine is null && endLine is null)
        {
            return (0, text.Length, true);
        }

        if (startLine is null || endLine is null || startLine <= 0 || endLine < startLine)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        if (startLine > starts.Count || endLine > starts.Count)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        var start = starts[startLine.Value - 1];
        var end = endLine.Value < starts.Count ? starts[endLine.Value] : text.Length;
        return (start, end, start == 0 && end == text.Length);
    }

    private static int LineStart(string text, int position)
    {
        while (position > 0 && text[position - 1] is not '\r' and not '\n')
        {
            position--;
        }

        return position;
    }

    private static int LineEnd(string text, int position)
    {
        while (position < text.Length && text[position] is not '\r' and not '\n')
        {
            position++;
        }

        if (position < text.Length && text[position] == '\r')
        {
            position++;
            if (position < text.Length && text[position] == '\n')
            {
                position++;
            }
        }
        else if (position < text.Length && text[position] == '\n')
        {
            position++;
        }

        return position;
    }

    private static void ValidateLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (literal.Length == 0 || literal.Contains('\r') || literal.Contains('\n')
            || literal.Any(char.IsControl) || literal.Contains('*') || literal.Contains('?'))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }
    }

    private static string NormalizeRepositoryPath(string path, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        try
        {
            _ = DocumentationScribeContextValidation.NormalizeRepositoryPath(path);
        }
        catch (ArgumentException)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        if (path.Contains('\\')
            || Path.IsPathRooted(path)
            || path.Contains(':')
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.EndsWith("/", StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        return path;
    }

    private static bool IsWithin(string root, string path) =>
        root.Length == 0
        || string.Equals(root, path, StringComparison.Ordinal)
        || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string FullPath(string root, string repositoryPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Join(
            fullRoot,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.ScopeDenied);
        }

        return candidate;
    }

    private static string RepositoryPath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static bool SplitsSurrogate(string text, int position) =>
        position > 0 && position < text.Length
        && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ToolFailure Failure(string code) => new(code);

    private static bool TryFailure(
        Exception exception,
        out DocumentationScribeToolOutcome outcome,
        out string code)
    {
        if (exception is OperationCanceledException)
        {
            outcome = DocumentationScribeToolOutcome.Cancelled;
            code = DocumentationScribeRepositoryToolFailureCodes.Cancelled;
            return true;
        }

        if (exception is ToolFailure failure)
        {
            code = failure.Code;
            outcome = code switch
            {
                DocumentationScribeRepositoryToolFailureCodes.Budget => DocumentationScribeToolOutcome.BudgetExhausted,
                DocumentationScribeRepositoryToolFailureCodes.Timeout => DocumentationScribeToolOutcome.TimedOut,
                DocumentationScribeRepositoryToolFailureCodes.Unavailable => DocumentationScribeToolOutcome.Unavailable,
                _ => DocumentationScribeToolOutcome.Failure,
            };
            return true;
        }

        if (exception is DocumentationScribeContextReadException readFailure)
        {
            code = readFailure.Failure switch
            {
                DocumentationScribeContextReadFailure.Budget => DocumentationScribeRepositoryToolFailureCodes.Budget,
                DocumentationScribeContextReadFailure.Unsafe => DocumentationScribeRepositoryToolFailureCodes.UnsafeObject,
                _ => DocumentationScribeRepositoryToolFailureCodes.Stale,
            };
            outcome = code == DocumentationScribeRepositoryToolFailureCodes.Budget
                ? DocumentationScribeToolOutcome.BudgetExhausted
                : DocumentationScribeToolOutcome.Failure;
            return true;
        }

        if (exception is IOException or UnauthorizedAccessException)
        {
            outcome = DocumentationScribeToolOutcome.Failure;
            code = DocumentationScribeRepositoryToolFailureCodes.Stale;
            return true;
        }

        if (exception is OverflowException)
        {
            outcome = DocumentationScribeToolOutcome.BudgetExhausted;
            code = DocumentationScribeRepositoryToolFailureCodes.Budget;
            return true;
        }

        if (exception is ArgumentException)
        {
            outcome = DocumentationScribeToolOutcome.Failure;
            code = DocumentationScribeRepositoryToolFailureCodes.InvalidRequest;
            return true;
        }

        if (exception is InvalidOperationException invalidOperation
            && invalidOperation.Message.StartsWith("context.cursor.", StringComparison.Ordinal))
        {
            outcome = DocumentationScribeToolOutcome.Failure;
            code = string.Equals(
                invalidOperation.Message,
                "context.cursor.invalid",
                StringComparison.Ordinal)
                ? DocumentationScribeRepositoryToolFailureCodes.InvalidCursor
                : DocumentationScribeRepositoryToolFailureCodes.Stale;
            return true;
        }

        outcome = null!;
        code = null!;
        return false;
    }

    private sealed class ToolFailure(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }

    private sealed record BoundScope(
        DocumentationScribeRepositoryToolScope Scope,
        DocumentationScribeInstructionContextFact? Origin);

    private sealed record StableTextFile(
        string RepositoryPath,
        byte[] RawBytes,
        string Text,
        string ContentSha256,
        bool HasBom);

    private sealed record Observation(
        string FullPath,
        string RepositoryPath,
        DocumentationScribeContextPhysicalIdentity Identity,
        ImmutableArray<DocumentationScribeContextDirectoryObservation> DirectoryChain,
        string ContentSha256);

    private sealed record NormalizedQuery(string Path, string? Literal, int PageSize);

    private sealed record PageValue(string Key, object Value, StableTextFile? Source);

    private sealed record PageResult(ImmutableArray<PageValue> Values, string? Cursor, bool HasMore);

    private sealed record PageChain(
        string OperationId,
        string ScopeId,
        NormalizedQuery Query,
        string Fingerprint,
        DocumentationScribeContextCursorScope CursorScope);
}
