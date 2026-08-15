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

    internal DocumentationScribeLoadedContext(
        ClassifiedRepositorySession classifiedSession,
        DocumentationScribeContextBootstrapSelection selection,
        DocumentationScribeContextFacts facts,
        DocumentationScribeContextCursorAuthority cursorAuthority)
    {
        this.classifiedSession = classifiedSession;
        this.selection = selection;
        Facts = facts;
        this.cursorAuthority = cursorAuthority;
    }

    public DocumentationScribeContextFacts Facts { get; }

    internal LoadedRepositorySession RepositorySession => classifiedSession.RepositorySession;

    internal bool IsCurrent =>
        classifiedSession.IsBoundToClassificationSession
        && !classifiedSession.RepositorySession.IsDisposed
        && classifiedSession.RepositorySession.RepositoryContextRef == Facts.RepositoryContextRef;

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
            || request.Target.SourceLocator is not RepositoryEvidenceLocator locator
            || locator != selection.SourceLocator
            || !string.Equals(request.Target.SourceSha256, selection.SourceSha256, StringComparison.Ordinal))
        {
            return InvalidBinding("context.binding.request-mismatch");
        }

        foreach (var instruction in Facts.Instructions)
        {
            var matches = request.ContextReferences.Count(reference =>
                reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction
                && reference.RepositoryContextRef == Facts.RepositoryContextRef
                && string.Equals(
                    reference.Path,
                    instruction.Commitment.Path,
                    StringComparison.Ordinal)
                && string.Equals(
                    reference.ContentSha256,
                    instruction.Commitment.ContentSha256,
                    StringComparison.Ordinal)
                && reference.OriginalUtf8ByteCount
                    == instruction.Commitment.OriginalUtf8ByteCount
                && reference.IncludedUtf8ByteCount
                    == instruction.Commitment.IncludedUtf8ByteCount
                && reference.IsTruncated == instruction.Commitment.IsTruncated);
            if (matches != 1)
            {
                return InvalidBinding("context.binding.instruction-set-mismatch");
            }
        }

        return new DocumentationScribeContextRequestBindingResult(true, null);
    }

    internal DocumentationScribeContextCursor IssueCursor(
        DocumentationScribeContextCursorScope scope,
        int nextPosition)
    {
        if (!IsCurrent || !ScopeMatchesContext(scope))
        {
            throw new InvalidOperationException("context.cursor.stale-session");
        }

        return cursorAuthority.Issue(scope, nextPosition);
    }

    internal bool TryValidateCursor(
        DocumentationScribeContextCursor cursor,
        DocumentationScribeContextCursorScope scope,
        out int nextPosition)
    {
        nextPosition = 0;
        return IsCurrent
            && ScopeMatchesContext(scope)
            && cursorAuthority.TryValidate(cursor, scope, out nextPosition);
    }

    public override string ToString() =>
        $"{nameof(DocumentationScribeLoadedContext)} {{ RepositoryContextRef = {Facts.RepositoryContextRef}, ContentIdentity = {Facts.ContentIdentity}, Instructions = {Facts.Instructions.Length}, Projects = {Facts.Projects.Length}, Evidence = {Facts.Evidence.Length}, CursorKey = <private> }}";

    private bool ScopeMatchesContext(DocumentationScribeContextCursorScope scope) =>
        scope.RepositoryContextRef == Facts.RepositoryContextRef
        && scope.SymbolRef == Facts.SymbolRef;

    private static DocumentationScribeContextRequestBindingResult InvalidBinding(string code) =>
        new(false, code);
}

public sealed class DocumentationScribeContextBootstrapper
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Action<DocumentationScribeContextBootstrapStage>? observer;
    private readonly Func<long> clock;
    private readonly Func<int, byte[]> randomBytes;

    public DocumentationScribeContextBootstrapper()
        : this(null, () => Environment.TickCount64, RandomNumberGenerator.GetBytes)
    {
    }

    internal DocumentationScribeContextBootstrapper(
        Action<DocumentationScribeContextBootstrapStage>? observer,
        Func<long>? clock = null,
        Func<int, byte[]>? randomBytes = null)
    {
        this.observer = observer;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.randomBytes = randomBytes ?? RandomNumberGenerator.GetBytes;
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
            var observations = new List<AcceptedFileObservation>();
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
            var sourceContent = TruncateUtf8(
                selectedSource.Text,
                includedLimit - (selectedSource.HasUtf8Bom ? 3 : 0),
                cancellationToken);
            var includedSourceBytes = StrictUtf8.GetByteCount(sourceContent)
                + (selectedSource.HasUtf8Bom ? 3 : 0);
            if (includedSourceBytes <= (selectedSource.HasUtf8Bom ? 3 : 0)
                && selectedSource.Text.Length > 0)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.total-bytes");
            }

            var sourceCommitment = DocumentationScribeContextValidation.CreateSourceCommitment(
                selection.SourceLocator.Path,
                selectedSource.Sha256,
                selectedSource.Bytes.Length,
                includedSourceBytes,
                includedSourceBytes < selectedSource.Bytes.Length,
                selectedSource.HasUtf8Bom);
            var sourceEvidence = DocumentationScribeContextValidation.CreateEvidenceFact(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                selection.SymbolRef.CompilationContextRef
                    + "|" + selection.SymbolRef.DocumentationCommentId,
                "source.target-declaration",
                sourceCommitment,
                sourceContent,
                selection.SourceLocator.Span!.Value.Start,
                selection.SourceLocator.Span.Value.End);
            var projectFact = DocumentationScribeContextValidation.CreateProjectFact(
                correlated.Project!.ProjectIdentity,
                correlated.Project.TargetFramework,
                correlated.Project.CompilationContextRef,
                correlated.Project.Role == LoadedProjectRole.AuditRoot
                    ? DocumentationScribeContextProjectRole.AuditRoot
                    : DocumentationScribeContextProjectRole.DependencyOnly,
                correlated.Project.ProjectReferences);
            var facts = DocumentationScribeContextValidation.CreateFacts(
                selection,
                discovery.Instructions,
                [projectFact],
                [sourceEvidence],
                discovery.Routes,
                discovery.Omissions);

            VerifyPublicationState(
                root,
                rootIdentity,
                discovery.AbsentPaths,
                observations,
                classifiedSession,
                selection,
                started,
                cancellationToken);
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
            var loaded = new DocumentationScribeLoadedContext(
                classifiedSession,
                selection,
                facts,
                cursorAuthority);
            return DocumentationScribeContextBootstrapResult.Accepted(
                discovery.Omissions.IsEmpty
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

    private static ScopeResolution ResolveScope(
        LoadedRepositorySession repository,
        LoadedProject project,
        DocumentationScribeContextBootstrapSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(
                selection.SymbolRef.DocumentationCommentId,
                project.Compilation)
            .Select(CanonicalPartialSymbol)
            .Distinct(SymbolEqualityComparer.Default)
            .ToArray();
        if (symbols.Length != 1)
        {
            return ScopeResolution.Rejected("context.scope.symbol-ambiguous");
        }

        var references = AuthoritativeReferences(symbols[0])
            .Distinct(SyntaxReferenceComparer.Instance)
            .ToArray();
        var sources = new List<ResolvedDeclarationSource>();
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var physicalScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchorMatched = false;
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.SourceTrees.TryGetValue(reference.SyntaxTree, out var source)
                || source.Kind != LoadedSourceKind.Repository
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
            anchorMatched |= string.Equals(
                    normalizedPath,
                    selection.SourceLocator.Path,
                    StringComparison.Ordinal)
                && selection.SourceLocator.Span is { } selectedSpan
                && reference.Span.Start == selectedSpan.Start
                && reference.Span.End == selectedSpan.End;
            sources.Add(new ResolvedDeclarationSource(
                normalizedPath,
                physicalIdentity,
                reference.SyntaxTree.GetText(cancellationToken).ToString()));
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
        List<AcceptedFileObservation> observations,
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

            var sha = Sha256(read.Bytes, cancellationToken);
            if (string.Equals(path, selection.SourceLocator.Path, StringComparison.Ordinal))
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
        List<AcceptedFileObservation> observations,
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
            totalBytes = checked(totalBytes + read.Bytes.Length);
            if (totalBytes > selection.Limits.MaximumTotalContextUtf8Bytes)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Budget,
                    "context.budget.total-bytes");
            }

            var commitment = DocumentationScribeContextValidation.CreateSourceCommitment(
                candidate.RepositoryPath,
                Sha256(read.Bytes, cancellationToken),
                read.Bytes.Length,
                read.Bytes.Length,
                false,
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
                destination.Commitment.Path,
                destination.Role,
                DocumentationScribeContextRouteSelection.DeterministicBootstrap,
                destination.Depth,
                destination.Commitment));
        }

        var omissions = selection.ConfiguredAgentEntrypoint is null
            && instructions.All(instruction => !string.Equals(
                instruction.Commitment.Path,
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
        List<AcceptedFileObservation> observations,
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        var fullPath = FullPath(root, repositoryPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "context.unsafe.repository-object");
        var beforeParent = DocumentationScribeContextStableFileReader
            .ReadDirectoryIdentity(parent);
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
            cancellationToken);
        Observe(
            DocumentationScribeContextBootstrapStage.Read,
            selection,
            started,
            cancellationToken);
        var afterParent = DocumentationScribeContextStableFileReader
            .ReadDirectoryIdentity(parent);
        if (beforeParent != afterParent
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
        observations.Add(new AcceptedFileObservation(fullPath, read.Identity));
        return read;
    }

    private void VerifyPublicationState(
        string root,
        DocumentationScribeContextPhysicalIdentity rootIdentity,
        ImmutableArray<string> absentPaths,
        IReadOnlyList<AcceptedFileObservation> observations,
        ClassifiedRepositorySession classifiedSession,
        DocumentationScribeContextBootstrapSelection selection,
        long started,
        CancellationToken cancellationToken)
    {
        Check(selection, started, cancellationToken);
        if (!classifiedSession.IsBoundToClassificationSession
            || classifiedSession.RepositorySession.IsDisposed
            || classifiedSession.RepositorySession.RepositoryContextRef
                != selection.RepositoryContextRef
            || DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root)
                != rootIdentity
            || absentPaths.Any(
                DocumentationScribeContextStableFileReader.RegularFileExistsNoFollow))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Stale,
                "context.stale.publication");
        }

        foreach (var observation in observations)
        {
            Check(selection, started, cancellationToken);
            if (DocumentationScribeContextStableFileReader
                    .ReadRegularFileIdentity(observation.FullPath)
                != observation.Identity)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "context.stale.publication");
            }
        }
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

    private static string TruncateUtf8(
        string value,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        if (maximumUtf8Bytes <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var remaining = value.AsSpan();
        var consumedBytes = 0;
        while (!remaining.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = Rune.DecodeFromUtf16(
                remaining,
                out var rune,
                out var consumedCharacters);
            if (status != OperationStatus.Done)
            {
                throw new DecoderFallbackException("Invalid UTF-16 content.");
            }

            if (consumedBytes + rune.Utf8SequenceLength > maximumUtf8Bytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            consumedBytes += rune.Utf8SequenceLength;
            remaining = remaining[consumedCharacters..];
        }

        return builder.ToString();
    }

    private static string Sha256(byte[] bytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FullPath(string root, string repositoryPath)
    {
        var candidate = Path.GetFullPath(Path.Join(
            root,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
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
        IMethodSymbol method => method.PartialImplementationPart
            ?? method.PartialDefinitionPart
            ?? method,
        _ => symbol,
    };

    private static IEnumerable<SyntaxReference> AuthoritativeReferences(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            var definition = method.PartialDefinitionPart ?? method;
            foreach (var reference in definition.DeclaringSyntaxReferences)
            {
                yield return reference;
            }

            if (definition.PartialImplementationPart is { } implementation)
            {
                foreach (var reference in implementation.DeclaringSyntaxReferences)
                {
                    yield return reference;
                }
            }

            yield break;
        }

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            yield return reference;
        }
    }

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

    private sealed record AcceptedFileObservation(
        string FullPath,
        DocumentationScribeContextPhysicalIdentity Identity);

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
