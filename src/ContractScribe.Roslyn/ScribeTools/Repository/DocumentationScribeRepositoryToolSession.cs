using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly Action<DocumentationScribeRepositoryToolCheckpoint>? checkpoint;
    private readonly ImmutableDictionary<string, BoundScope> scopes;
    private readonly string runCorrelation = Guid.NewGuid().ToString("N");
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<(ulong Volume, ulong FileId), string> physicalPaths = [];
    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AbsenceObservation> absences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DocumentationScribeContextPhysicalIdentity> directoryObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageChain> cursorChains = new(StringComparer.Ordinal);
    private readonly HashSet<string> consumedCursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> inspectedFilePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> inspectedDirectoryPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);
    private long bytesRead;
    private long returnedBytes;
    private int returnedItems;
    private int directoriesInspected;
    private int entriesInspected;
    private int filesInspected;
    private int matchesDiscovered;
    private int activeChains;

    internal DocumentationScribeRepositoryToolSession(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeLoadedContext loadedContext,
        IEnumerable<DocumentationScribeRepositoryToolScope> scopes,
        DocumentationScribeRepositoryToolLimits limits,
        Action<DocumentationScribeRepositoryToolCheckpoint>? checkpoint)
    {
        this.request = request;
        this.attemptId = attemptId;
        this.loadedContext = loadedContext;
        this.limits = limits;
        this.checkpoint = checkpoint;
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

                var call = BeginCall(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt, cancellationToken);
                var scope = GetScope(toolRequest.ScopeId, DocumentationScribeRepositoryToolOperations.ReadExcerpt);
                var path = ResolveFile(scope, toolRequest.RepositoryPath);
                var file = ReadFile(path, scope.Scope.Required, call, cancellationToken);
                var range = SelectLines(file.Text, toolRequest.StartLine, toolRequest.EndLine);
                var excerpt = CreateExcerpt(file, range.Start, range.End);
                Checkpoint(DocumentationScribeRepositoryToolCheckpoint.BeforeEvidencePublication, cancellationToken);
                var route = CreateRoute(scope, file, cancellationToken);
                var evidence = CreateEvidence(scope, file, range.Start, range.End);
                var result = new DocumentationScribeRepositoryReadExcerptResult(
                    excerpt.IsTruncated ? DocumentationScribeToolOutcome.Incomplete : DocumentationScribeToolOutcome.Complete,
                    null,
                    excerpt,
                    route,
                    evidence is null ? [] : [evidence]);
                CommitPublication(MeasurePublication(result), 1, cancellationToken);
                return result;
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

                var call = BeginCall(DocumentationScribeRepositoryToolOperationIds.ListFiles, cancellationToken);
                var scope = GetScope(toolRequest.ScopeId, DocumentationScribeRepositoryToolOperations.ListFiles);
                var query = NormalizeQuery(scope, toolRequest.Subdirectory, null, toolRequest.PageSize);
                var page = Page(
                    DocumentationScribeRepositoryToolOperationIds.ListFiles,
                    scope,
                    query,
                    toolRequest.Cursor,
                    cancellationToken,
                    () => ListInventory(scope, query.Path, call, cancellationToken),
                    null,
                    (values, cursor, hasMore) => MeasurePublication(new DocumentationScribeRepositoryListFilesResult(
                        hasMore ? DocumentationScribeToolOutcome.Incomplete : DocumentationScribeToolOutcome.Complete,
                        null,
                        values.Select(value => (DocumentationScribeRepositoryFileItem)value.Value).ToImmutableArray(),
                        cursor)));
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

                var call = BeginCall(DocumentationScribeRepositoryToolOperationIds.SearchText, cancellationToken);
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
                    () => Search(scope, query.Path, toolRequest.Literal, call, cancellationToken),
                    values => PrepareSearchPublication(
                        scope, values, routes, evidence, cancellationToken),
                    (values, cursor, hasMore) => MeasurePublication(new DocumentationScribeRepositorySearchTextResult(
                        hasMore ? DocumentationScribeToolOutcome.Incomplete : DocumentationScribeToolOutcome.Complete,
                        null,
                        values.Select(value => (DocumentationScribeRepositoryExcerpt)value.Value).ToImmutableArray(),
                        cursor,
                        routes.ToImmutable(),
                        evidence.ToImmutable())));
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
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var file = (StableTextFile)value.Source!;
            var item = (DocumentationScribeRepositoryExcerpt)value.Value;
            if (CreateRoute(scope, file, cancellationToken) is { } route
                && routeIds.Add(route.RouteId))
            {
                routes.Add(route);
            }

            if (CreateEvidence(scope, file, item.StartUtf16, item.EndUtf16) is { } row
                && DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, row, out var reference)
                && evidenceIds.Add(reference!.EvidenceReferenceId))
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

        ValidateEvidenceScope(scope, anchorPath);

        return new(scope with { RepositoryPath = path }, origin);
    }

    private void ValidateEvidenceScope(DocumentationScribeRepositoryToolScope scope, string anchorPath)
    {
        if (scope.Subject is null)
        {
            return;
        }

        var roleMatches = scope.Role switch
        {
            DocumentationScribeContextRole.MaintainedDocumentation =>
                scope.Kind == EvidenceKind.RepositoryDocumentation
                    && scope.Relation == EvidenceRelation.Documents
                    && scope.Authority == DocumentationScribeEvidenceAuthority.RepositoryDocumentation
                || scope.Kind == EvidenceKind.PublicContract
                    && scope.Relation == EvidenceRelation.Constrains
                    && scope.Authority == DocumentationScribeEvidenceAuthority.PublicContract,
            DocumentationScribeContextRole.SourceDeclaration =>
                scope.Kind == EvidenceKind.SourceDeclaration
                    && scope.Relation == EvidenceRelation.Declares
                    && scope.Authority == DocumentationScribeEvidenceAuthority.SourceDeclaration
                || scope.Kind == EvidenceKind.SourceImplementation
                    && scope.Relation is EvidenceRelation.Declares or EvidenceRelation.References
                    && scope.Authority == DocumentationScribeEvidenceAuthority.SourceImplementation
                || scope.Kind == EvidenceKind.SourceXmlDocumentation
                    && scope.Relation == EvidenceRelation.Documents
                    && scope.Authority == DocumentationScribeEvidenceAuthority.ExistingDocumentation
                || scope.Kind == EvidenceKind.SourceAttribute
                    && scope.Relation == EvidenceRelation.Constrains
                    && scope.Authority == DocumentationScribeEvidenceAuthority.SourceDeclaration,
            DocumentationScribeContextRole.TestEvidence =>
                scope.Kind == EvidenceKind.Test
                    && scope.Relation == EvidenceRelation.Tests
                    && scope.Authority == DocumentationScribeEvidenceAuthority.Test,
            DocumentationScribeContextRole.UsageEvidence =>
                scope.Kind == EvidenceKind.SourceImplementation
                    && scope.Relation == EvidenceRelation.References
                    && scope.Authority == DocumentationScribeEvidenceAuthority.SourceImplementation,
            _ => false,
        };
        var probe = new DocumentationScribeDynamicEvidenceInput(
            scope.Subject,
            scope.Kind,
            scope.Relation,
            scope.Authority,
            EvidenceInput.RepositoryLocator(anchorPath),
            new string('a', 64),
            1,
            1,
            false,
            scope.ClaimCategoryIds);
        if (!roleMatches || !DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, probe, out _))
        {
            throw new ArgumentException("The repository evidence scope is contradictory or not request-authorized.", nameof(scope));
        }
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

    private CallBudget BeginCall(string operationId, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        calls.TryGetValue(operationId, out var count);
        count = checked(count + 1);
        calls[operationId] = count;
        if (count > limits.MaximumCallsPerOperation)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }

        return new CallBudget();
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

    private PageMaterialization ListInventory(
        BoundScope scope,
        string path,
        CallBudget call,
        CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(scope, path, call, cancellationToken);
        var values = ImmutableArray.CreateBuilder<PageValue>(files.Length);
        var candidates = ImmutableArray.CreateBuilder<string>(files.Length);
        foreach (var enumerated in files)
        {
            var file = ReadFile(
                enumerated.RepositoryPath,
                scope.Scope.Required,
                call,
                cancellationToken,
                enumerated.FullPath);
            var item = new DocumentationScribeRepositoryFileItem(
                enumerated.RepositoryPath,
                file.ContentSha256,
                file.RawBytes.Length);
            var key = CandidateKey(file);
            candidates.Add(key);
            values.Add(new(key, item, null));
        }

        return new(values.ToImmutable(), candidates.ToImmutable());
    }

    private PageMaterialization Search(
        BoundScope scope,
        string path,
        string literal,
        CallBudget call,
        CancellationToken cancellationToken)
    {
        var paths = scope.Scope.IsDirectory
            ? EnumerateFiles(scope, path, call, cancellationToken)
            : ImmutableArray.Create(new EnumeratedFile(
                scope.Scope.RepositoryPath,
                ResolveExactProviderPath(scope.Scope.RepositoryPath, call, cancellationToken)));
        var builder = ImmutableArray.CreateBuilder<PageValue>();
        var candidates = ImmutableArray.CreateBuilder<string>(paths.Length);
        foreach (var enumerated in paths)
        {
            var repositoryPath = enumerated.RepositoryPath;
            var file = ReadFile(repositoryPath, scope.Scope.Required, call, cancellationToken, enumerated.FullPath);
            candidates.Add(CandidateKey(file));
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

                ChargeMatch();

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

        return new(
            builder.OrderBy(value => value.Key, StringComparer.Ordinal).ToImmutableArray(),
            candidates.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    private ImmutableArray<EnumeratedFile> EnumerateFiles(
        BoundScope scope,
        string path,
        CallBudget call,
        CancellationToken cancellationToken)
    {
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var start = ResolveExactProviderPath(path, call, cancellationToken);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((start, 0));
        var files = new List<EnumeratedFile>();
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            Check(cancellationToken);
            var (directory, depth) = pending.Pop();
            var directoryRepositoryPath = RepositoryPath(root, directory);
            if (absences.TryGetValue(directoryRepositoryPath, out var absence))
            {
                var currentChain = DocumentationScribeContextDirectoryChain.Read(root, Path.GetDirectoryName(directory)!);
                if (Directory.Exists(directory) || File.Exists(directory)
                    || !currentChain.SequenceEqual(absence.DirectoryChain))
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
                }

                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }

            if (depth > limits.MaximumDirectoryDepth)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
            }

            ChargeDirectory(directoryRepositoryPath);

            try
            {
                var identity = DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(directory);
                if (directoryObservations.TryGetValue(directoryRepositoryPath, out var accepted)
                    && accepted != identity)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
                }

                directoryObservations[directoryRepositoryPath] = identity;
            }
            catch (DocumentationScribeContextReadException exception) when (
                !scope.Scope.Required
                && exception.Failure == DocumentationScribeContextReadFailure.Stale)
            {
                if (!Directory.Exists(directory) && !File.Exists(directory))
                {
                    var parent = Path.GetDirectoryName(directory)!;
                    absences[directoryRepositoryPath] = new(
                        directory,
                        DocumentationScribeContextDirectoryChain.Read(root, parent));
                }

                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }
            List<string> entries = [];
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    Check(cancellationToken);
                    ChargeEntry(call);

                    entries.Add(entry);
                }
            }
            catch (DirectoryNotFoundException)
            {
                throw Failure(scope.Scope.Required
                    ? DocumentationScribeRepositoryToolFailureCodes.Stale
                    : DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }

            entries.Sort(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                Check(cancellationToken);
                var repositoryPath = RepositoryPath(root, entry);
                _ = NormalizeRepositoryPath(repositoryPath, allowEmpty: false);
                if (spellings.TryGetValue(repositoryPath, out var existing)
                    && !string.Equals(existing, repositoryPath, StringComparison.Ordinal))
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
                }

                spellings[repositoryPath] = repositoryPath;

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

                if (scope.Scope.Extensions.Length != 0
                    && !scope.Scope.Extensions.Contains(Path.GetExtension(repositoryPath), StringComparer.Ordinal))
                {
                    continue;
                }

                files.Add(new(repositoryPath, entry));
                ChargeFile(repositoryPath);
                if (files.Count > limits.MaximumFilesPerCall)
                {
                    throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
                }
            }
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RepositoryPath, right.RepositoryPath));
        return files.ToImmutableArray();
    }

    private StableTextFile ReadFile(
        string repositoryPath,
        bool required,
        CallBudget call,
        CancellationToken cancellationToken,
        string? exactFullPath = null)
    {
        Check(cancellationToken);
        ChargeFile(repositoryPath);
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var fullPath = exactFullPath ?? ResolveExactProviderPath(repositoryPath, call, cancellationToken);
        var parent = Path.GetDirectoryName(fullPath)!;
        if (absences.TryGetValue(repositoryPath, out var absence))
        {
            var currentChain = DocumentationScribeContextDirectoryChain.Read(root, parent);
            if (File.Exists(fullPath) || Directory.Exists(fullPath)
                || !currentChain.SequenceEqual(absence.DirectoryChain))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Unavailable);
        }

        var before = DocumentationScribeContextDirectoryChain.Read(root, parent);
        DocumentationScribeContextStableRead read;
        try
        {
            read = DocumentationScribeContextStableFileReader.ReadRegularFileAnchored(
                root,
                fullPath,
                limits.MaximumFileUtf8Bytes,
                cancellationToken,
                () => Check(cancellationToken));
        }
        catch (DocumentationScribeContextReadException exception)
        {
            var previouslyAccepted = observations.ContainsKey(repositoryPath);
            if (!required && !previouslyAccepted
                && exception.Failure == DocumentationScribeContextReadFailure.Stale
                && !File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                var chain = DocumentationScribeContextDirectoryChain.Read(root, parent);
                absences[repositoryPath] = new(fullPath, chain);
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Unavailable);
            }

            throw Failure(exception.Failure switch
            {
                DocumentationScribeContextReadFailure.Budget when previouslyAccepted => DocumentationScribeRepositoryToolFailureCodes.Stale,
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
        var sha = Sha256(read.Bytes);
        if (observations.TryGetValue(repositoryPath, out var accepted)
            && (accepted.Identity != read.Identity
                || !accepted.DirectoryChain.SequenceEqual(after)
                || !string.Equals(accepted.ContentSha256, sha, StringComparison.Ordinal)))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
        }

        observations[repositoryPath] = new(fullPath, repositoryPath, read.Identity, after, sha);
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

        return new(repositoryPath, read.Bytes, text, sha, HasBom(read.Bytes));
    }

    private PageResult Page(
        string operationId,
        BoundScope scope,
        NormalizedQuery query,
        string? cursorValue,
        CancellationToken cancellationToken,
        Func<PageMaterialization> materialize,
        Action<ImmutableArray<PageValue>>? preparePublication,
        Func<ImmutableArray<PageValue>, string?, bool, int> measurePublication)
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
            var materialization = materialize();
            var values = materialization.Values;
            Checkpoint(DocumentationScribeRepositoryToolCheckpoint.AfterMaterialization, cancellationToken);
            var fingerprint = Fingerprint(materialization);
            if (chain is not null && !string.Equals(chain.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            if (position > values.Length)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }

            Checkpoint(DocumentationScribeRepositoryToolCheckpoint.BeforeFinalMembershipCheck, cancellationToken);
            var finalMaterialization = materialize();
            var finalValues = finalMaterialization.Values;
            if (!string.Equals(fingerprint, Fingerprint(finalMaterialization), StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            values = finalValues;
            chain ??= CreateChain(operationId, scope, query, fingerprint);
            var count = Math.Min(query.PageSize, values.Length - position);
            var page = values.Skip(position).Take(count).ToImmutableArray();
            var hasMore = position + count < values.Length;
            preparePublication?.Invoke(page);
            VerifyFresh(cancellationToken);
            Checkpoint(DocumentationScribeRepositoryToolCheckpoint.BeforeCursorPublication, cancellationToken);
            if (!string.Equals(fingerprint, Fingerprint(materialize()), StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            VerifyFresh(cancellationToken);
            var next = loadedContext.IssueCursor(chain.CursorScope, current, count, hasMore, cancellationToken);
            if (next is { } candidate
                && (cursorChains.ContainsKey(candidate.Value)
                    || consumedCursors.Contains(candidate.Value)))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidCursor);
            }

            CommitPublication(measurePublication(page, next?.Value, hasMore), page.Length, cancellationToken);
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
            file.HasBom || start != 0 || end != file.Text.Length,
            matchStart,
            matchEnd);
    }

    private DocumentationScribeDynamicEvidenceInput? CreateEvidence(
        BoundScope scope,
        StableTextFile file,
        int start,
        int end)
    {
        if (scope.Scope.Subject is null)
        {
            return null;
        }

        var content = file.Text[start..end];
        var included = StrictUtf8.GetByteCount(content);
        var original = file.RawBytes.Length;
        var complete = start == 0 && end == file.Text.Length && !file.HasBom;
        var locator = complete
            ? EvidenceInput.RepositoryLocator(file.RepositoryPath)
            : EvidenceInput.RepositoryLocator(file.RepositoryPath, start, end);
        var input = new DocumentationScribeDynamicEvidenceInput(
            scope.Scope.Subject,
            scope.Scope.Kind,
            scope.Scope.Relation,
            scope.Scope.Authority,
            locator,
            file.ContentSha256,
            original,
            included,
            !complete,
            scope.Scope.ClaimCategoryIds);
        if (!DocumentationScribeValidation.TryCreateDynamicEvidenceReference(request, input, out _))
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        return input;
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
            var before = DocumentationScribeContextDirectoryChain.Read(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                Path.GetDirectoryName(observation.FullPath)!);
            var read = DocumentationScribeContextStableFileReader.ReadRegularFileAnchored(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                observation.FullPath,
                limits.MaximumFileUtf8Bytes,
                cancellationToken,
                () => Check(cancellationToken));
            ChargeBytes(read.Bytes.Length);
            Checkpoint(DocumentationScribeRepositoryToolCheckpoint.AfterFreshRead, cancellationToken);
            var after = DocumentationScribeContextDirectoryChain.Read(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                Path.GetDirectoryName(observation.FullPath)!);
            if (read.Identity != observation.Identity
                || !before.SequenceEqual(observation.DirectoryChain)
                || !after.SequenceEqual(observation.DirectoryChain)
                || !before.SequenceEqual(after)
                || !string.Equals(Sha256(read.Bytes), observation.ContentSha256, StringComparison.Ordinal))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }

            if (!loadedContext.VerifyFreshness(cancellationToken))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }
        }

        foreach (var absence in absences.Values)
        {
            Check(cancellationToken);
            var parent = Path.GetDirectoryName(absence.FullPath)!;
            var directories = DocumentationScribeContextDirectoryChain.Read(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                parent);
            if (File.Exists(absence.FullPath) || Directory.Exists(absence.FullPath)
                || !directories.SequenceEqual(absence.DirectoryChain))
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }
        }

        foreach (var directory in directoryObservations)
        {
            Check(cancellationToken);
            var fullPath = FullPath(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                directory.Key == "." ? string.Empty : directory.Key);
            if (DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(fullPath) != directory.Value)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.Stale);
            }
        }
    }

    internal static int MeasurePublication<T>(T value) => value switch
    {
        DocumentationScribeRepositoryReadExcerptResult result => checked(
            512 + MeasureExcerpt(result.Excerpt) + MeasureRoute(result.Route)
            + result.DynamicEvidence.Sum(MeasureEvidence)),
        DocumentationScribeRepositoryListFilesResult result => checked(
            512 + MeasureText(result.Cursor)
            + result.Items.Sum(item => 256 + MeasureText(item.RepositoryPath) + MeasureText(item.ContentSha256))),
        DocumentationScribeRepositorySearchTextResult result => checked(
            768 + MeasureText(result.Cursor) + result.Items.Sum(MeasureExcerpt)
            + result.Routes.Sum(MeasureRoute) + result.DynamicEvidence.Sum(MeasureEvidence)),
        _ => throw new InvalidOperationException("Unsupported repository tool publication type."),
    };

    private static int MeasureExcerpt(DocumentationScribeRepositoryExcerpt? value) => value is null
        ? 4
        : checked(384 + MeasureText(value.RepositoryPath) + MeasureText(value.Content)
            + MeasureText(value.ContentSha256));

    private static int MeasureRoute(DocumentationScribeInstructionRouteFact? value) => value is null
        ? 4
        : checked(1_024 + MeasureText(value.RouteId) + MeasureText(value.OriginInstructionId)
            + MeasureText(value.DestinationPath) + MeasureText(value.SourceCommitment.RepositoryPath)
            + MeasureText(value.SourceCommitment.ContentSha256)
            + MeasureText(value.SourceCommitment.IncludedContentSha256));

    private static int MeasureEvidence(DocumentationScribeDynamicEvidenceInput value)
    {
        var subject = value.Subject.ParentSymbolRef;
        var componentIdentity = value.Subject is ComponentEvidenceSubject component
            ? MeasureText(component.Identity)
            : 0;
        var locator = value.Locator is RepositoryEvidenceLocator repository
            ? MeasureText(repository.Path)
            : MeasureText(value.Locator.ToString());
        return checked(
            1_024 + MeasureText(subject.CompilationContextRef) + MeasureText(subject.DocumentationCommentId)
            + componentIdentity + locator + MeasureText(value.ContentSha256)
            + value.ClaimCategoryIds.Sum(MeasureText));
    }

    internal static int MeasureJsonStringUtf8Bytes(string? value) => value is null
        ? 4
        : checked(JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length + 2);

    private static int MeasureText(string? value) => MeasureJsonStringUtf8Bytes(value);

    private void Checkpoint(
        DocumentationScribeRepositoryToolCheckpoint value,
        CancellationToken cancellationToken)
    {
        checkpoint?.Invoke(value);
        Check(cancellationToken);
    }

    private static string Fingerprint(PageMaterialization materialization)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        WriteFingerprintRows(stream, length, "candidate", materialization.CandidateKeys);
        WriteFingerprintRows(stream, length, "value", materialization.Values.Select(value => value.Key));
        return Sha256(stream.ToArray());
    }

    private static void WriteFingerprintRows(
        Stream stream,
        Span<byte> length,
        string kind,
        IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var bytes = StrictUtf8.GetBytes(string.Join('\0', kind, value));
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
    }

    private void ChargeEntry(CallBudget call)
    {
        call.EntriesInspected = checked(call.EntriesInspected + 1);
        entriesInspected = checked(entriesInspected + 1);
        if (call.EntriesInspected > limits.MaximumEntriesPerCall
            || entriesInspected > limits.MaximumEntriesPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }
    }

    private void ChargeDirectory(string repositoryPath)
    {
        if (!inspectedDirectoryPaths.Add(repositoryPath))
        {
            return;
        }

        directoriesInspected = checked(directoriesInspected + 1);
        if (directoriesInspected > limits.MaximumDirectoriesPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }
    }

    private void ChargeFile(string repositoryPath)
    {
        if (!inspectedFilePaths.Add(repositoryPath))
        {
            return;
        }

        filesInspected = checked(filesInspected + 1);
        if (filesInspected > limits.MaximumFilesPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
        }
    }

    private void ChargeMatch()
    {
        matchesDiscovered = checked(matchesDiscovered + 1);
        if (matchesDiscovered > limits.MaximumMatchesPerRun)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.Budget);
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
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(literal);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(DocumentationScribeRepositoryToolFailureCodes.InvalidRequest);
        }

        var scalars = literal.EnumerateRunes().Count();
        if (literal.Length == 0 || literal.Contains('\r') || literal.Contains('\n')
            || literal.Any(char.IsControl) || scalars > 1_024 || bytes > 4_096)
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
            || path.Any(char.IsControl)
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

    private string ResolveExactProviderPath(
        string repositoryPath,
        CallBudget call,
        CancellationToken cancellationToken)
    {
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var current = Path.GetFullPath(root);
        if (repositoryPath.Length == 0)
        {
            return current;
        }

        foreach (var segment in repositoryPath.Split('/'))
        {
            Check(cancellationToken);
            string? exact = null;
            var alternate = false;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    ChargeEntry(call);
                    var name = Path.GetFileName(entry);
                    if (string.Equals(name, segment, StringComparison.Ordinal))
                    {
                        exact = entry;
                    }
                    else if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        alternate = true;
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                return FullPath(root, repositoryPath);
            }

            if (alternate)
            {
                throw Failure(DocumentationScribeRepositoryToolFailureCodes.UnsafeObject);
            }

            if (exact is null)
            {
                return FullPath(root, repositoryPath);
            }

            current = exact;
        }

        return current;
    }

    private static string RepositoryPath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static string CandidateKey(StableTextFile file) => string.Join('\0',
        file.RepositoryPath,
        file.ContentSha256,
        file.RawBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        file.HasBom ? "bom" : "no-bom");

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

    private sealed record EnumeratedFile(string RepositoryPath, string FullPath);

    private sealed class CallBudget
    {
        internal int EntriesInspected { get; set; }
    }

    private sealed record Observation(
        string FullPath,
        string RepositoryPath,
        DocumentationScribeContextPhysicalIdentity Identity,
        ImmutableArray<DocumentationScribeContextDirectoryObservation> DirectoryChain,
        string ContentSha256);

    private sealed record AbsenceObservation(
        string FullPath,
        ImmutableArray<DocumentationScribeContextDirectoryObservation> DirectoryChain);

    private sealed record NormalizedQuery(string Path, string? Literal, int PageSize);

    private sealed record PageValue(string Key, object Value, StableTextFile? Source);

    private sealed record PageMaterialization(
        ImmutableArray<PageValue> Values,
        ImmutableArray<string> CandidateKeys);

    private sealed record PageResult(ImmutableArray<PageValue> Values, string? Cursor, bool HasMore);

    private sealed record PageChain(
        string OperationId,
        string ScopeId,
        NormalizedQuery Query,
        string Fingerprint,
        DocumentationScribeContextCursorScope CursorScope);
}
