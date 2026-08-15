using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Roslyn;

internal enum DocumentationScribeContextBootstrapStage
{
    Correlation,
    ScopeResolution,
    Open,
    Read,
    Decode,
    Normalize,
    Cursor,
    Publish,
}

public sealed class DocumentationScribeContextBootstrapResult
{
    private DocumentationScribeContextBootstrapResult(
        DocumentationScribeContextBootstrapStatus status,
        DocumentationScribeLoadedContext? context,
        DocumentationScribeContextFailure? failure,
        ImmutableArray<DocumentationScribeContextDiagnostic> diagnostics)
    {
        Status = status;
        Context = context;
        Failure = failure;
        Diagnostics = diagnostics;
    }

    public DocumentationScribeContextBootstrapStatus Status { get; }

    public DocumentationScribeLoadedContext? Context { get; }

    public DocumentationScribeContextFailure? Failure { get; }

    public ImmutableArray<DocumentationScribeContextDiagnostic> Diagnostics { get; }

    internal static DocumentationScribeContextBootstrapResult Accepted(
        DocumentationScribeContextBootstrapStatus status,
        DocumentationScribeLoadedContext context) =>
        new(status, context, null, context.Facts.Diagnostics);

    internal static DocumentationScribeContextBootstrapResult Rejected(
        DocumentationScribeContextBootstrapStatus status,
        DocumentationScribeContextFailure? failure) =>
        new(status, null, failure, []);

    public override string ToString() =>
        $"{nameof(DocumentationScribeContextBootstrapResult)} {{ Status = {Status}, HasContext = {Context is not null}, FailureCode = {Failure?.Code ?? "none"}, Diagnostics = {Diagnostics.Length} }}";
}

public sealed record DocumentationScribeContextRequestBindingResult
{
    internal DocumentationScribeContextRequestBindingResult(bool isValid, string? failureCode)
    {
        IsValid = isValid;
        FailureCode = failureCode;
    }

    public bool IsValid { get; }

    public string? FailureCode { get; }
}

public sealed class DocumentationScribeLoadedContext
{
    private readonly ClassifiedRepositorySession classifiedSession;
    private readonly DocumentationScribeContextBootstrapSelection selection;
    private readonly DocumentationScribeContextCursorAuthority cursorAuthority;
    private readonly DocumentationScribeContextFreshnessGuard freshnessGuard;
    private readonly Action? cursorPublicationObserver;

    internal DocumentationScribeLoadedContext(
        ClassifiedRepositorySession classifiedSession,
        DocumentationScribeContextBootstrapSelection selection,
        DocumentationScribeContextFacts facts,
        DocumentationScribeContextCursorAuthority cursorAuthority,
        DocumentationScribeContextFreshnessGuard freshnessGuard,
        Action? cursorPublicationObserver)
    {
        this.classifiedSession = classifiedSession;
        this.selection = selection;
        Facts = facts;
        this.cursorAuthority = cursorAuthority;
        this.freshnessGuard = freshnessGuard;
        this.cursorPublicationObserver = cursorPublicationObserver;
    }

    public DocumentationScribeContextFacts Facts { get; }

    internal LoadedRepositorySession RepositorySession => classifiedSession.RepositorySession;

    internal bool IsCurrent =>
        classifiedSession.IsBoundToClassificationSession
        && !classifiedSession.RepositorySession.IsDisposed
        && freshnessGuard.HasNotFailed
        && classifiedSession.RepositorySession.RepositoryContextRef == Facts.RepositoryContextRef;

    internal bool VerifyFreshness(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent)
        {
            return false;
        }

        return freshnessGuard.TryExecuteIfFresh(
            cancellationToken,
            () => IsCurrent);
    }

    public DocumentationScribeContextRequestBindingResult ValidateRequestBinding(
        DocumentationScribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCurrent)
        {
            return InvalidBinding("context.binding.stale-session");
        }

        if (request.Context.RepositoryContextRef != Facts.RepositoryContextRef
            || !string.Equals(request.Context.InputIdentity, Facts.InputIdentity, StringComparison.Ordinal)
            || request.Context.TargetProfile != Facts.TargetProfile
            || request.Target.SymbolRef != Facts.SymbolRef
            || request.Target.SourceLocator != selection.SourceLocator
            || !string.Equals(request.Target.SourceSha256, selection.SourceSha256, StringComparison.Ordinal))
        {
            return InvalidBinding("context.binding.request-mismatch");
        }

        var expectedInstructions = Facts.Instructions
            .Select(instruction => new InstructionBinding(
                Facts.RepositoryContextRef,
                instruction.Commitment.RepositoryPath!,
                instruction.Commitment.ContentSha256,
                instruction.Commitment.OriginalUtf8ByteCount,
                instruction.Commitment.IncludedUtf8ByteCount,
                instruction.Commitment.IsTruncated))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ContentSha256, StringComparer.Ordinal)
            .ToArray();
        var requestInstructions = request.ContextReferences
            .Where(reference => reference.Kind
                == DocumentationScribeContextReferenceKind.ProjectInstruction)
            .Select(reference => new InstructionBinding(
                reference.RepositoryContextRef,
                reference.Path,
                reference.ContentSha256,
                reference.OriginalUtf8ByteCount,
                reference.IncludedUtf8ByteCount,
                reference.IsTruncated))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ContentSha256, StringComparer.Ordinal)
            .ToArray();
        if (!expectedInstructions.SequenceEqual(requestInstructions))
        {
            return InvalidBinding("context.binding.instruction-set-mismatch");
        }

        return new DocumentationScribeContextRequestBindingResult(true, null);
    }

    internal DocumentationScribeContextCursor? IssueCursor(
        DocumentationScribeContextCursorScope scope,
        DocumentationScribeContextCursor? currentCursor,
        int returnedItemCount,
        bool hasMore,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (returnedItemCount < 0
            || returnedItemCount > scope.PageSize
            || hasMore && returnedItemCount != scope.PageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(returnedItemCount));
        }

        try
        {
            return freshnessGuard.ExecuteIfFresh<DocumentationScribeContextCursor?>(
                cancellationToken,
                () =>
                {
                    if (!IsCurrent || !ScopeMatchesContext(scope))
                    {
                        throw new InvalidOperationException("context.cursor.stale-session");
                    }

                    var currentPosition = 0;
                    if (currentCursor is { } cursor
                        && !cursorAuthority.TryValidate(cursor, scope, out currentPosition))
                    {
                        throw new InvalidOperationException("context.cursor.invalid");
                    }

                    cursorPublicationObserver?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!hasMore)
                    {
                        return null;
                    }

                    var issued = cursorAuthority.Issue(
                        scope,
                        checked(currentPosition + returnedItemCount));
                    cancellationToken.ThrowIfCancellationRequested();
                    return issued;
                });
        }
        catch (DocumentationScribeContextReadException exception)
        {
            throw new InvalidOperationException("context.cursor.stale-session", exception);
        }
    }

    internal bool TryValidateCursor(
        DocumentationScribeContextCursor cursor,
        DocumentationScribeContextCursorScope scope,
        out int nextPosition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nextPosition = 0;
        if (!IsCurrent || !ScopeMatchesContext(scope))
        {
            return false;
        }

        var candidatePosition = 0;
        var isValid = freshnessGuard.TryExecuteIfFresh(
            cancellationToken,
            () =>
            {
                if (!IsCurrent || !ScopeMatchesContext(scope))
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = cursorAuthority.TryValidate(
                    cursor,
                    scope,
                    out candidatePosition);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            });
        if (isValid)
        {
            nextPosition = candidatePosition;
        }

        return isValid;
    }

    public override string ToString() =>
        $"{nameof(DocumentationScribeLoadedContext)} {{ Instructions = {Facts.Instructions.Length}, Projects = {Facts.Projects.Length}, Evidence = {Facts.Evidence.Length}, CursorKey = <private> }}";

    private bool ScopeMatchesContext(DocumentationScribeContextCursorScope scope) =>
        scope.RepositoryContextRef == Facts.RepositoryContextRef
        && scope.SymbolRef == Facts.SymbolRef
        && string.Equals(
            scope.SourceCommitmentsSha256,
            DocumentationScribeContextValidation.ComputeCommitmentsSha256(
                Facts.Instructions.Select(item => item.Commitment)
                    .Concat(Facts.Evidence.Select(item => item.Commitment))),
            StringComparison.Ordinal);

    private static DocumentationScribeContextRequestBindingResult InvalidBinding(string code) =>
        new(false, code);

    private sealed record InstructionBinding(
        RepositoryContextRef RepositoryContextRef,
        string Path,
        string ContentSha256,
        int OriginalUtf8ByteCount,
        int IncludedUtf8ByteCount,
        bool IsTruncated);
}

internal sealed record DocumentationScribeContextAcceptedFileObservation(
    string FullPath,
    int MaximumBytes,
    ImmutableArray<DocumentationScribeContextDirectoryObservation> DirectoryChain,
    DocumentationScribeContextPhysicalIdentity Identity,
    string ContentSha256);

internal sealed record DocumentationScribeContextDirectoryObservation(
    string FullPath,
    DocumentationScribeContextPhysicalIdentity Identity);

internal static class DocumentationScribeContextDirectoryChain
{
    internal static ImmutableArray<DocumentationScribeContextDirectoryObservation> Read(
        string root,
        string directory)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedDirectory = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedDirectory);
        var chain = ImmutableArray.CreateBuilder<DocumentationScribeContextDirectoryObservation>();
        chain.Add(new(
            normalizedRoot,
            DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(normalizedRoot)));
        if (relative == ".")
        {
            return chain.ToImmutable();
        }

        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            chain.Add(new(
                current,
                DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(current)));
        }

        return chain.ToImmutable();
    }
}

internal sealed class DocumentationScribeContextFreshnessGuard
{
    private readonly string root;
    private readonly DocumentationScribeContextPhysicalIdentity rootIdentity;
    private readonly ImmutableArray<string> absentPaths;
    private readonly ImmutableArray<DocumentationScribeContextAcceptedFileObservation> observations;
    private readonly ClassifiedRepositorySession classifiedSession;
    private readonly RepositoryContextRef repositoryContextRef;
    private readonly Action? verificationCheckpoint;
    private readonly SemaphoreSlim verificationGate = new(1, 1);
    private int failed;

    internal DocumentationScribeContextFreshnessGuard(
        string root,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        IEnumerable<string> absentPaths,
        IEnumerable<DocumentationScribeContextAcceptedFileObservation> observations,
        ClassifiedRepositorySession classifiedSession,
        RepositoryContextRef repositoryContextRef,
        Action? verificationCheckpoint = null)
    {
        this.root = root;
        this.rootIdentity = rootIdentity;
        this.absentPaths = absentPaths.ToImmutableArray();
        this.observations = observations.ToImmutableArray();
        this.classifiedSession = classifiedSession;
        this.repositoryContextRef = repositoryContextRef;
        this.verificationCheckpoint = verificationCheckpoint;
    }

    internal bool HasNotFailed => Volatile.Read(ref failed) == 0;

    internal void VerifyOrThrow(
        CancellationToken cancellationToken = default,
        Action? checkpoint = null)
    {
        ExecuteIfFresh(cancellationToken, static () => true, checkpoint);
    }

    internal T ExecuteIfFresh<T>(
        CancellationToken cancellationToken,
        Func<T> operation,
        Action? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        verificationGate.Wait(cancellationToken);
        try
        {
            if (!HasNotFailed)
            {
                throw Stale();
            }

            try
            {
                VerifyState(cancellationToken, checkpoint);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Interlocked.Exchange(ref failed, 1);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
        finally
        {
            verificationGate.Release();
        }
    }

    internal bool TryExecuteIfFresh(
        CancellationToken cancellationToken,
        Func<bool> operation)
    {
        try
        {
            return ExecuteIfFresh(cancellationToken, operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void VerifyState(
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        Checkpoint(cancellationToken, checkpoint);
        if (!classifiedSession.IsBoundToClassificationSession
            || classifiedSession.RepositorySession.IsDisposed
            || classifiedSession.RepositorySession.RepositoryContextRef != repositoryContextRef
            || DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root)
                != rootIdentity
            || absentPaths.Any(
                DocumentationScribeContextStableFileReader.RegularFileExistsNoFollow))
        {
            throw Stale();
        }

        foreach (var observation in observations)
        {
            Checkpoint(cancellationToken, checkpoint);
            var directoryChain = DocumentationScribeContextDirectoryChain.Read(
                root,
                Path.GetDirectoryName(observation.FullPath)!);
            cancellationToken.ThrowIfCancellationRequested();
            if (!directoryChain.SequenceEqual(observation.DirectoryChain))
            {
                throw Stale();
            }

            var read = DocumentationScribeContextStableFileReader.ReadRegularFile(
                observation.FullPath,
                observation.MaximumBytes,
                cancellationToken,
                () => Checkpoint(cancellationToken, checkpoint));
            cancellationToken.ThrowIfCancellationRequested();
            var directoryChainAfter = DocumentationScribeContextDirectoryChain.Read(
                root,
                Path.GetDirectoryName(observation.FullPath)!);
            cancellationToken.ThrowIfCancellationRequested();
            var contentSha256 = Sha256(read.Bytes);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Identity != observation.Identity
                || !directoryChainAfter.SequenceEqual(observation.DirectoryChain)
                || DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root)
                    != rootIdentity
                || !string.Equals(
                    contentSha256,
                    observation.ContentSha256,
                    StringComparison.Ordinal))
            {
                throw Stale();
            }
        }
    }

    private void Checkpoint(
        CancellationToken cancellationToken,
        Action? checkpoint)
    {
        verificationCheckpoint?.Invoke();
        checkpoint?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DocumentationScribeContextReadException Stale() =>
        new(
            DocumentationScribeContextReadFailure.Stale,
            "context.stale.publication");
}

public sealed class DocumentationScribeContextBootstrapper
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Action<DocumentationScribeContextBootstrapStage>? observer;
    private readonly Func<long> clock;
    private readonly Func<int, byte[]> randomBytes;
    private readonly Action? freshnessCheckpoint;
    private readonly Action? cursorPublicationObserver;

    public DocumentationScribeContextBootstrapper()
        : this(null, () => Environment.TickCount64, RandomNumberGenerator.GetBytes)
    {
    }

    internal DocumentationScribeContextBootstrapper(
        Action<DocumentationScribeContextBootstrapStage>? observer,
        Func<long>? clock = null,
        Func<int, byte[]>? randomBytes = null,
        Action? freshnessCheckpoint = null,
        Action? cursorPublicationObserver = null)
    {
        this.observer = observer;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.randomBytes = randomBytes ?? RandomNumberGenerator.GetBytes;
        this.freshnessCheckpoint = freshnessCheckpoint;
        this.cursorPublicationObserver = cursorPublicationObserver;
    }

    public DocumentationScribeContextBootstrapResult Bootstrap(
        ClassifiedRepositorySession classifiedSession,
        DocumentationScribeContextBootstrapSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classifiedSession);
        ArgumentNullException.ThrowIfNull(selection);
        var started = clock();
        try
        {
            Observe(
                DocumentationScribeContextBootstrapStage.Correlation,
                selection,
                started,
                cancellationToken);
            var correlated = Correlate(classifiedSession, selection);
            if (correlated.Failure is { } correlationFailure)
            {
                return Reject(correlationFailure.Status, correlationFailure.Category, correlationFailure.Code);
            }

            Observe(
                DocumentationScribeContextBootstrapStage.ScopeResolution,
                selection,
                started,
                cancellationToken);
            var scope = ResolveScope(
                classifiedSession.RepositorySession,
                correlated.Project!,
                selection,
                started,
                cancellationToken);
            if (scope.Failure is { } scopeFailure)
            {
                return Reject(scopeFailure.Status, scopeFailure.Category, scopeFailure.Code);
            }

            Check(selection, started, cancellationToken);
            var root = classifiedSession.RepositorySession.PhysicalRepositoryRoot;
            var rootIdentity = DocumentationScribeContextStableFileReader
                .ReadDirectoryIdentity(root);
            var acceptedIdentities = new Dictionary<
                (ulong Volume, ulong FileId),
                string>();
            var observations = new List<DocumentationScribeContextAcceptedFileObservation>();
            var selectedSource = ReadAndValidateDeclarationSources(
                root,
                rootIdentity,
                scope.Sources,
                selection,
                acceptedIdentities,
                observations,
                started,
                cancellationToken);

            var discovery = DiscoverInstructions(
                root,
                rootIdentity,
                scope.RepositoryScope!,
                selection,
                acceptedIdentities,
                observations,
                started,
                cancellationToken);
            var totalInstructionBytes = discovery.Instructions
                .Sum(instruction => instruction.Commitment.IncludedUtf8ByteCount);
            var remaining = selection.Limits.MaximumTotalContextUtf8Bytes
                - totalInstructionBytes;
            if (remaining <= 0)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.total-bytes");
            }

            Observe(
                DocumentationScribeContextBootstrapStage.Normalize,
                selection,
                started,
                cancellationToken);
            var includedLimit = Math.Min(
                selection.Limits.MaximumIncludedSourceUtf8Bytes,
                remaining);
            var repositoryLocator = (RepositoryEvidenceLocator)selection.SourceLocator;
            var targetSpan = repositoryLocator.Span!.Value;
            if (targetSpan.End > selectedSource.Text.Length)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "context.stale.source-text");
            }

            string sourceContent;
            int? includedRangeStart;
            int? includedRangeEnd;
            var includesCompleteSource = selectedSource.Bytes.Length <= includedLimit;
            if (includesCompleteSource)
            {
                sourceContent = selectedSource.Text;
                includedRangeStart = 0;
                includedRangeEnd = selectedSource.Text.Length;
            }
            else
            {
                var declaration = selectedSource.Text[targetSpan.Start..targetSpan.End];
                if (StrictUtf8.GetByteCount(declaration) <= includedLimit)
                {
                    sourceContent = declaration;
                    includedRangeStart = targetSpan.Start;
                    includedRangeEnd = targetSpan.End;
                }
                else
                {
                    sourceContent = string.Empty;
                    includedRangeStart = null;
                    includedRangeEnd = null;
                }
            }

            var includedHasUtf8Bom = includesCompleteSource && selectedSource.HasUtf8Bom;
            var includedSourceBytes = StrictUtf8.GetByteCount(sourceContent)
                + (includedHasUtf8Bom ? 3 : 0);

            var sourceCommitment = DocumentationScribeContextValidation.CreateEvidenceSourceCommitment(
                selection.SourceLocator,
                selectedSource.Sha256,
                selectedSource.Bytes.Length,
                includedSourceBytes,
                includedSourceBytes < selectedSource.Bytes.Length,
                selectedSource.HasUtf8Bom,
                includedHasUtf8Bom);
            var sourceEvidence = DocumentationScribeContextValidation.CreateEvidenceFact(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "symbol." + DocumentationScribeContextValidation.ComputeSymbolRefSha256(
                    selection.SymbolRef),
                "source.target-declaration",
                sourceCommitment,
                sourceContent,
                targetSpan.Start,
                targetSpan.End,
                includedRangeStart,
                includedRangeEnd);
            var projectFact = DocumentationScribeContextValidation.CreateProjectFact(
                correlated.Project!.ProjectIdentity,
                correlated.Project.TargetFramework,
                correlated.Project.CompilationContextRef,
                correlated.Project.Role == LoadedProjectRole.AuditRoot
                    ? DocumentationScribeContextProjectRole.AuditRoot
                    : DocumentationScribeContextProjectRole.DependencyOnly,
                correlated.Project.ProjectReferences);
            var omissions = sourceCommitment.IsTruncated
                ? discovery.Omissions.Add(
                    DocumentationScribeContextValidation.CreateOmission(
                        DocumentationScribeContextRole.SourceDeclaration,
                        repositoryLocator.Path,
                        DocumentationScribeContextOmissionReason.ByteLimit))
                : discovery.Omissions;
            var facts = DocumentationScribeContextValidation.CreateFacts(
                selection,
                discovery.Instructions,
                [projectFact],
                [sourceEvidence],
                discovery.Routes,
                omissions);
            var freshnessGuard = new DocumentationScribeContextFreshnessGuard(
                root,
                rootIdentity,
                discovery.AbsentPaths,
                observations,
                classifiedSession,
                selection.RepositoryContextRef,
                freshnessCheckpoint);
            Observe(
                DocumentationScribeContextBootstrapStage.Cursor,
                selection,
                started,
                cancellationToken);
            var cursorAuthority = new DocumentationScribeContextCursorAuthority(
                randomBytes(32));
            Observe(
                DocumentationScribeContextBootstrapStage.Publish,
                selection,
                started,
                cancellationToken);
            freshnessGuard.VerifyOrThrow(
                cancellationToken,
                () => Check(selection, started, cancellationToken));
            var loaded = new DocumentationScribeLoadedContext(
                classifiedSession,
                selection,
                facts,
                cursorAuthority,
                freshnessGuard,
                cursorPublicationObserver);
            return DocumentationScribeContextBootstrapResult.Accepted(
                omissions.IsEmpty
                    ? DocumentationScribeContextBootstrapStatus.Succeeded
                    : DocumentationScribeContextBootstrapStatus.Incomplete,
                loaded);
        }
        catch (OperationCanceledException)
        {
            return DocumentationScribeContextBootstrapResult.Rejected(
                DocumentationScribeContextBootstrapStatus.Cancelled,
                null);
        }
        catch (DocumentationScribeContextTimeoutException)
        {
            return Reject(
                DocumentationScribeContextBootstrapStatus.TimedOut,
                DocumentationScribeContextFailureCategory.Internal,
                "context.timeout.operation");
        }
        catch (DocumentationScribeContextReadException exception)
        {
            return exception.Failure switch
            {
                DocumentationScribeContextReadFailure.Budget => Reject(
                    DocumentationScribeContextBootstrapStatus.BudgetExhausted,
                    DocumentationScribeContextFailureCategory.Internal,
                    exception.Code),
                DocumentationScribeContextReadFailure.Stale => Reject(
                    DocumentationScribeContextBootstrapStatus.Failed,
                    DocumentationScribeContextFailureCategory.Stale,
                    exception.Code),
                _ => Reject(
                    DocumentationScribeContextBootstrapStatus.Failed,
                    DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
                    exception.Code),
            };
        }
        catch (DecoderFallbackException)
        {
            return Reject(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.InvalidEncoding,
                "context.invalid-encoding");
        }
        catch (InvalidOperationException exception) when (
            string.Equals(exception.Message, "context.identity-collision", StringComparison.Ordinal))
        {
            return Reject(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.IdentityCollision,
                "context.identity-collision");
        }
        catch
        {
            return Reject(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.Internal,
                "context.internal-error");
        }
    }

    private static CorrelationResult Correlate(
        ClassifiedRepositorySession classifiedSession,
        DocumentationScribeContextBootstrapSelection selection)
    {
        var repository = classifiedSession.RepositorySession;
        if (!classifiedSession.IsBoundToClassificationSession
            || repository.IsDisposed
            || classifiedSession.Classification.Status != ClassificationRunStatus.Success
            || classifiedSession.Classification.ClassificationSet is not { } classifications)
        {
            return CorrelationResult.Rejected(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.Correlation,
                "context.correlation.session");
        }

        if (repository.RepositoryContextRef != selection.RepositoryContextRef
            || !string.Equals(repository.InputIdentity, selection.InputIdentity, StringComparison.Ordinal)
            || classifications.TargetProfile != selection.TargetProfile)
        {
            return CorrelationResult.Rejected(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.Correlation,
                "context.correlation.request");
        }

        var projects = repository.Projects
            .Where(project => string.Equals(
                project.CompilationContextRef,
                selection.CompilationContextRef,
                StringComparison.Ordinal))
            .ToArray();
        if (projects.Length != 1)
        {
            return CorrelationResult.Rejected(
                DocumentationScribeContextBootstrapStatus.Failed,
                DocumentationScribeContextFailureCategory.Correlation,
                "context.correlation.compilation");
        }

        var targets = classifications.Targets
            .Where(target => target.SymbolRef == selection.SymbolRef)
            .ToArray();
        if (targets.Length != 1
            || targets[0].SupportStatus != SupportStatus.Supported
            || targets[0].Origin == ClassificationOrigin.Mixed)
        {
            return CorrelationResult.Rejected(
                DocumentationScribeContextBootstrapStatus.Unavailable,
                DocumentationScribeContextFailureCategory.AmbiguousScope,
                "context.scope.target-unavailable");
        }

        return new CorrelationResult(projects[0], targets[0], null);
    }

    private ScopeResolution ResolveScope(
        LoadedRepositorySession repository,
        LoadedProject project,
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        Check(selection, started, cancellationToken);
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var candidate in DocumentationCommentId.GetSymbolsForDeclarationId(
                     selection.SymbolRef.DocumentationCommentId,
                     project.Compilation))
        {
            Check(selection, started, cancellationToken);
            symbols.Add(CanonicalPartialSymbol(candidate));
            if (symbols.Count > 1)
            {
                return ScopeResolution.Rejected("context.scope.symbol-ambiguous");
            }
        }

        if (symbols.Count != 1)
        {
            return ScopeResolution.Rejected("context.scope.symbol-ambiguous");
        }

        var references = new List<SyntaxReference>();
        var distinctReferences = new HashSet<SyntaxReference>(SyntaxReferenceComparer.Instance);
        foreach (var reference in AuthoritativeReferences(symbols.Single()))
        {
            Check(selection, started, cancellationToken);
            if (!distinctReferences.Add(reference))
            {
                continue;
            }

            if (references.Count >= selection.Limits.MaximumDeclarationReferences)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.declaration-references");
            }

            references.Add(reference);
        }

        var sources = new List<ResolvedDeclarationSource>();
        var loadedTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var physicalScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchorMatched = false;
        var inspectedBytes = 0;
        foreach (var reference in references)
        {
            Check(selection, started, cancellationToken);
            if (!project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source))
            {
                continue;
            }

            anchorMatched |= LocatorMatchesReference(selection.SourceLocator, source, reference);
            if (source.Kind != LoadedSourceKind.Repository
                || source.RepositoryPath is not { } repositoryPath
                || source.PhysicalSourceIdentity is not { } physicalIdentity)
            {
                continue;
            }

            var normalizedPath = DocumentationScribeContextValidation
                .NormalizeRepositoryPath(repositoryPath);
            var scope = RepositoryDirectory(normalizedPath);
            scopes.Add(scope);
            if (physicalScopes.TryGetValue(physicalIdentity, out var existingScope)
                && !string.Equals(existingScope, scope, StringComparison.Ordinal))
            {
                return ScopeResolution.Rejected("context.scope.physical-alias");
            }

            physicalScopes[physicalIdentity] = scope;
            if (!loadedTexts.ContainsKey(normalizedPath))
            {
                if (loadedTexts.Count >= selection.Limits.MaximumDeclarationFiles)
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Budget,
                        "context.budget.declaration-files");
                }

                var sourceText = reference.SyntaxTree.GetText(cancellationToken);
                var remainingInspectedBytes = selection.Limits.MaximumInspectedSourceUtf8Bytes
                    - inspectedBytes;
                if (sourceText.Length > remainingInspectedBytes)
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Budget,
                        "context.budget.inspected-source-bytes");
                }

                var loadedText = sourceText.ToString();
                Check(selection, started, cancellationToken);
                inspectedBytes = checked(inspectedBytes + StrictUtf8.GetByteCount(loadedText));
                if (inspectedBytes > selection.Limits.MaximumInspectedSourceUtf8Bytes)
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Budget,
                        "context.budget.inspected-source-bytes");
                }

                loadedTexts.Add(normalizedPath, loadedText);
                sources.Add(new ResolvedDeclarationSource(
                    normalizedPath,
                    physicalIdentity,
                    loadedText));
            }
        }

        if (!anchorMatched || scopes.Count != 1 || sources.Count == 0)
        {
            return ScopeResolution.Rejected("context.scope.not-unique");
        }

        return new ScopeResolution(scopes.Single(), sources.ToImmutableArray(), null);
    }

    private SelectedSource ReadAndValidateDeclarationSources(
        string root,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        ImmutableArray<ResolvedDeclarationSource> sources,
        DocumentationScribeContextBootstrapSelection selection,
        Dictionary<(ulong Volume, ulong FileId), string> acceptedIdentities,
        List<DocumentationScribeContextAcceptedFileObservation> observations,
        long started,
        CancellationToken cancellationToken)
    {
        SelectedSource? selected = null;
        foreach (var group in sources
                     .GroupBy(source => source.RepositoryPath, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var path = group.Key;
            var read = ReadAuthorizedFile(
                root,
                rootIdentity,
                path,
                selection.Limits.MaximumSourceFileUtf8Bytes,
                acceptedIdentities,
                observations,
                selection,
                started,
                cancellationToken);
            Observe(
                DocumentationScribeContextBootstrapStage.Decode,
                selection,
                started,
                cancellationToken);
            var text = Decode(read.Bytes, out var hasBom);
            var loadedTexts = group
                .Select(source => source.LoadedText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (loadedTexts.Length != 1
                || !string.Equals(text, loadedTexts[0], StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "context.stale.source-text");
            }

            foreach (var source in group)
            {
                var expectedPhysical = new RepositoryPathResolver().PhysicalIdentity(
                    root,
                    FullPath(root, source.RepositoryPath));
                if (!string.Equals(
                    expectedPhysical,
                    source.LoadedPhysicalIdentity,
                    StringComparison.Ordinal))
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Unsafe,
                        "context.unsafe.source-alias");
                }
            }

            var sha = Sha256(
                read.Bytes,
                cancellationToken,
                () => Check(selection, started, cancellationToken));
            if (selection.SourceLocator is RepositoryEvidenceLocator repositoryLocator
                && string.Equals(path, repositoryLocator.Path, StringComparison.Ordinal))
            {
                if (!string.Equals(sha, selection.SourceSha256, StringComparison.Ordinal))
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Stale,
                        "context.stale.source-commitment");
                }

                selected = new SelectedSource(read.Bytes, text, sha, hasBom);
            }
        }

        return selected ?? throw new DocumentationScribeContextReadException(
            DocumentationScribeContextReadFailure.Stale,
            "context.stale.source-commitment");
    }

    private InstructionDiscovery DiscoverInstructions(
        string root,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryScope,
        DocumentationScribeContextBootstrapSelection selection,
        Dictionary<(ulong Volume, ulong FileId), string> acceptedIdentities,
        List<DocumentationScribeContextAcceptedFileObservation> observations,
        long started,
        CancellationToken cancellationToken)
    {
        var segments = string.IsNullOrEmpty(repositoryScope)
            ? Array.Empty<string>()
            : repositoryScope.Split('/');
        if (segments.Length > selection.Limits.MaximumInstructionDepth)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Budget,
                "context.budget.instruction-depth");
        }

        var candidates = new List<InstructionCandidate>();
        var absent = ImmutableArray.CreateBuilder<string>();
        if (selection.ConfiguredAgentEntrypoint is { } configured)
        {
            var fullConfigured = FullPath(root, configured);
            if (!DocumentationScribeContextStableFileReader.RegularFileExistsNoFollow(
                    fullConfigured))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "context.stale.configured-entrypoint");
            }

            candidates.Add(new InstructionCandidate(
                configured,
                DocumentationScribeContextRole.AgentEntrypoint,
                0));
        }
        else
        {
            var rootAgent = FullPath(root, "AGENTS.md");
            if (DocumentationScribeContextStableFileReader.RegularFileExistsNoFollow(rootAgent))
            {
                candidates.Add(new InstructionCandidate(
                    "AGENTS.md",
                    DocumentationScribeContextRole.AgentEntrypoint,
                    0));
            }
            else
            {
                absent.Add(rootAgent);
            }
        }

        var prefix = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            prefix = string.IsNullOrEmpty(prefix)
                ? segments[index]
                : prefix + "/" + segments[index];
            var path = prefix + "/AGENTS.md";
            if (string.Equals(
                path,
                selection.ConfiguredAgentEntrypoint,
                StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = FullPath(root, path);
            if (DocumentationScribeContextStableFileReader.RegularFileExistsNoFollow(fullPath))
            {
                candidates.Add(new InstructionCandidate(
                    path,
                    DocumentationScribeContextRole.ScopedInstruction,
                    index + 1));
            }
            else
            {
                absent.Add(fullPath);
            }
        }

        if (candidates.Count > selection.Limits.MaximumInstructionFiles)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Budget,
                "context.budget.instruction-files");
        }

        var facts = ImmutableArray.CreateBuilder<DocumentationScribeInstructionContextFact>();
        var totalBytes = 0;
        foreach (var candidate in candidates)
        {
            var read = ReadAuthorizedFile(
                root,
                rootIdentity,
                candidate.RepositoryPath,
                selection.Limits.MaximumInstructionFileUtf8Bytes,
                acceptedIdentities,
                observations,
                selection,
                started,
                cancellationToken);
            Observe(
                DocumentationScribeContextBootstrapStage.Decode,
                selection,
                started,
                cancellationToken);
            var text = Decode(read.Bytes, out var hasBom);
            var decodedByteCount = StrictUtf8.GetByteCount(text);
            var includedByteCount = decodedByteCount + (hasBom ? 3 : 0);
            totalBytes = checked(totalBytes + includedByteCount);
            if (totalBytes > selection.Limits.MaximumTotalContextUtf8Bytes)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.total-bytes");
            }

            var commitment = DocumentationScribeContextValidation.CreateSourceCommitment(
                candidate.RepositoryPath,
                Sha256(
                    read.Bytes,
                    cancellationToken,
                    () => Check(selection, started, cancellationToken)),
                read.Bytes.Length,
                includedByteCount,
                false,
                hasBom,
                hasBom);
            facts.Add(DocumentationScribeContextValidation.CreateInstructionFact(
                candidate.Role,
                candidate.Depth,
                commitment,
                text));
        }

        var instructions = facts.ToImmutable();
        var routes = ImmutableArray.CreateBuilder<DocumentationScribeInstructionRouteFact>();
        for (var index = 1; index < instructions.Length; index++)
        {
            var destination = instructions[index];
            routes.Add(DocumentationScribeContextValidation.CreateInstructionRoute(
                instructions[index - 1].InstructionId,
                destination.Commitment.RepositoryPath!,
                destination.Role,
                DocumentationScribeContextRouteSelection.DeterministicBootstrap,
                destination.Depth,
                destination.Commitment));
        }

        var omissions = selection.ConfiguredAgentEntrypoint is null
            && instructions.All(instruction => !string.Equals(
                instruction.Commitment.RepositoryPath!,
                "AGENTS.md",
                StringComparison.Ordinal))
            ? ImmutableArray.Create(DocumentationScribeContextValidation.CreateOmission(
                DocumentationScribeContextRole.AgentEntrypoint,
                "AGENTS.md",
                DocumentationScribeContextOmissionReason.MissingOptional))
            : ImmutableArray<DocumentationScribeContextOmissionFact>.Empty;
        return new InstructionDiscovery(
            instructions,
            routes.ToImmutable(),
            omissions,
            absent.ToImmutable());
    }

    private DocumentationScribeContextStableRead ReadAuthorizedFile(
        string root,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        string repositoryPath,
        int maximumBytes,
        Dictionary<(ulong Volume, ulong FileId), string> acceptedIdentities,
        List<DocumentationScribeContextAcceptedFileObservation> observations,
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        var fullPath = FullPath(root, repositoryPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "context.unsafe.repository-object");
        var beforeDirectoryChain = DocumentationScribeContextDirectoryChain.Read(root, parent);
        if (DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root) != rootIdentity)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Stale,
                "context.stale.repository-root");
        }

        Observe(
            DocumentationScribeContextBootstrapStage.Open,
            selection,
            started,
            cancellationToken);
        var read = DocumentationScribeContextStableFileReader.ReadRegularFile(
            fullPath,
            maximumBytes,
            cancellationToken,
            () => Check(selection, started, cancellationToken));
        Observe(
            DocumentationScribeContextBootstrapStage.Read,
            selection,
            started,
            cancellationToken);
        var afterDirectoryChain = DocumentationScribeContextDirectoryChain.Read(root, parent);
        if (!beforeDirectoryChain.SequenceEqual(afterDirectoryChain)
            || DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root) != rootIdentity
            || read.Identity.LinkCount != 1)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "context.unsafe.physical-identity");
        }

        var identity = (read.Identity.Volume, read.Identity.FileId);
        if (acceptedIdentities.TryGetValue(identity, out var acceptedPath)
            && !string.Equals(acceptedPath, repositoryPath, StringComparison.Ordinal))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "context.unsafe.physical-alias");
        }

        acceptedIdentities[identity] = repositoryPath;
        observations.Add(new DocumentationScribeContextAcceptedFileObservation(
            fullPath,
            maximumBytes,
            afterDirectoryChain,
            read.Identity,
            Sha256(
                read.Bytes,
                cancellationToken,
                () => Check(selection, started, cancellationToken))));
        return read;
    }

    private void Observe(
        DocumentationScribeContextBootstrapStage stage,
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        Check(selection, started, cancellationToken);
        observer?.Invoke(stage);
        Check(selection, started, cancellationToken);
    }

    private void Check(
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock() - started > selection.Limits.MaximumElapsedMilliseconds)
        {
            throw new DocumentationScribeContextTimeoutException();
        }
    }

    private static string Decode(byte[] bytes, out bool hasBom)
    {
        hasBom = bytes.Length >= 3
            && bytes[0] == 0xef
            && bytes[1] == 0xbb
            && bytes[2] == 0xbf;
        return StrictUtf8.GetString(hasBom ? bytes.AsSpan(3) : bytes);
    }

    private static string Sha256(
        byte[] bytes,
        CancellationToken cancellationToken,
        Action? checkpoint = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
            hash.AppendData(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FullPath(string root, string repositoryPath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Join(
            normalizedRoot,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(normalizedRoot, candidate);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", comparison)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "context.unsafe.path");
        }

        return candidate;
    }

    private static string RepositoryDirectory(string repositoryPath)
    {
        var separator = repositoryPath.LastIndexOf('/');
        return separator < 0 ? string.Empty : repositoryPath[..separator];
    }

    private static ISymbol CanonicalPartialSymbol(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
        IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
        IEventSymbol { PartialDefinitionPart: { } definition } => definition,
        _ => symbol,
    };

    private static IEnumerable<SyntaxReference> AuthoritativeReferences(ISymbol symbol)
    {
        var definition = CanonicalPartialSymbol(symbol);
        foreach (var reference in definition.DeclaringSyntaxReferences)
        {
            yield return reference;
        }

        ISymbol? implementation = definition switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };
        if (implementation is null)
        {
            yield break;
        }

        foreach (var reference in implementation.DeclaringSyntaxReferences)
        {
            yield return reference;
        }
    }

    private static bool LocatorMatchesReference(
        EvidenceLocator locator,
        LoadedSourceTree source,
        SyntaxReference reference) => locator switch
        {
            RepositoryEvidenceLocator repository =>
                source.Kind == LoadedSourceKind.Repository
                && string.Equals(
                    source.RepositoryPath,
                    repository.Path,
                    StringComparison.Ordinal)
                && repository.Span is { } span
                && span.Start == reference.Span.Start
                && span.End == reference.Span.End,
            GeneratedOutputEvidenceLocator generated when source.GeneratedSource is { } fact =>
                (generated.ProducerKind == GeneratedOutputKind.SourceGenerator
                        && source.Kind == LoadedSourceKind.SourceGenerator
                    || generated.ProducerKind == GeneratedOutputKind.ToolGenerated
                        && source.Kind == LoadedSourceKind.ToolGenerated)
                && string.Equals(fact.ProducerId, generated.ProducerId, StringComparison.Ordinal)
                && string.Equals(fact.OutputId, generated.OutputId, StringComparison.Ordinal)
                && string.Equals(fact.SourceSha256, generated.SourceSha256, StringComparison.Ordinal)
                && generated.Span is { } span
                && span.Start == reference.Span.Start
                && span.End == reference.Span.End,
            _ => false,
        };

    private static DocumentationScribeContextBootstrapResult Reject(
        DocumentationScribeContextBootstrapStatus status,
        DocumentationScribeContextFailureCategory category,
        string code) =>
        DocumentationScribeContextBootstrapResult.Rejected(
            status,
            DocumentationScribeContextValidation.CreateFailure(category, code));

    private sealed record CorrelationResult(
        LoadedProject? Project,
        TargetClassification? Target,
        BootstrapFailure? Failure)
    {
        internal static CorrelationResult Rejected(
            DocumentationScribeContextBootstrapStatus status,
            DocumentationScribeContextFailureCategory category,
            string code) =>
            new(null, null, new BootstrapFailure(status, category, code));
    }

    private sealed record ScopeResolution(
        string? RepositoryScope,
        ImmutableArray<ResolvedDeclarationSource> Sources,
        BootstrapFailure? Failure)
    {
        internal static ScopeResolution Rejected(string code) =>
            new(
                null,
                [],
                new BootstrapFailure(
                    DocumentationScribeContextBootstrapStatus.Unavailable,
                    DocumentationScribeContextFailureCategory.AmbiguousScope,
                    code));
    }

    private sealed record BootstrapFailure(
        DocumentationScribeContextBootstrapStatus Status,
        DocumentationScribeContextFailureCategory Category,
        string Code);

    private sealed record ResolvedDeclarationSource(
        string RepositoryPath,
        string LoadedPhysicalIdentity,
        string LoadedText);

    private sealed record SelectedSource(
        byte[] Bytes,
        string Text,
        string Sha256,
        bool HasUtf8Bom);

    private sealed record InstructionCandidate(
        string RepositoryPath,
        DocumentationScribeContextRole Role,
        int Depth);

    private sealed record InstructionDiscovery(
        ImmutableArray<DocumentationScribeInstructionContextFact> Instructions,
        ImmutableArray<DocumentationScribeInstructionRouteFact> Routes,
        ImmutableArray<DocumentationScribeContextOmissionFact> Omissions,
        ImmutableArray<string> AbsentPaths);

    private sealed class SyntaxReferenceComparer : IEqualityComparer<SyntaxReference>
    {
        internal static SyntaxReferenceComparer Instance { get; } = new();

        public bool Equals(SyntaxReference? left, SyntaxReference? right) =>
            ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && ReferenceEquals(left.SyntaxTree, right.SyntaxTree)
                && left.Span == right.Span;

        public int GetHashCode(SyntaxReference value) =>
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.SyntaxTree),
                value.Span);
    }

    private sealed class DocumentationScribeContextTimeoutException : Exception;
}
