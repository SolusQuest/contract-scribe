using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.Roslyn;

internal enum DocumentationScribeSemanticStage
{
    Binding,
    Target,
    Documentation,
    Relations,
    Usages,
    Normalize,
    Page,
    FinalFreshness,
    Cursor,
    Publish,
}

public sealed class DocumentationScribeSemanticToolPort
    : IDocumentationScribeToolPort<
        DocumentationScribeSemanticToolRequest,
        DocumentationScribeSemanticToolResult>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DocumentationScribeLoadedContext loadedContext;
    private readonly DocumentationScribeRequest scribeRequest;
    private readonly DocumentationScribeSemanticToolLimits limits;
    private readonly Action<DocumentationScribeSemanticStage>? stageObserver;
    private readonly Func<string, string> identity;
    private readonly Binding binding;

    public DocumentationScribeSemanticToolPort(
        DocumentationScribeLoadedContext loadedContext,
        DocumentationScribeRequest scribeRequest,
        DocumentationScribeSemanticToolLimits? limits = null)
        : this(loadedContext, scribeRequest, limits, null, null)
    {
    }

    internal DocumentationScribeSemanticToolPort(
        DocumentationScribeLoadedContext loadedContext,
        DocumentationScribeRequest scribeRequest,
        DocumentationScribeSemanticToolLimits? limits,
        Action<DocumentationScribeSemanticStage>? stageObserver,
        Func<string, string>? identity)
    {
        this.loadedContext = loadedContext ?? throw new ArgumentNullException(nameof(loadedContext));
        this.scribeRequest = scribeRequest ?? throw new ArgumentNullException(nameof(scribeRequest));
        this.limits = Intersect(
            limits ?? DocumentationScribeSemanticToolLimits.Production,
            scribeRequest.Limits);
        this.stageObserver = stageObserver;
        this.identity = identity ?? Sha256;

        var requestBinding = loadedContext.ValidateRequestBinding(scribeRequest);
        if (!requestBinding.IsValid)
        {
            throw new ArgumentException(
                "semantic.binding.request-mismatch",
                nameof(scribeRequest));
        }

        binding = Bind(loadedContext, scribeRequest);
        if (binding.FailureReason is null
            && binding.Method is not null
            && !ApplicableComponentsEqual(
                binding.ApplicableComponents,
                scribeRequest.Target.ApplicableComponents))
        {
            throw new ArgumentException(
                "semantic.binding.applicable-components-mismatch",
                nameof(scribeRequest));
        }
    }

    public ValueTask<DocumentationScribeSemanticToolResult> InvokeAsync(
        DocumentationScribeSemanticToolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        try
        {
            Check(DocumentationScribeSemanticStage.Binding, started, cancellationToken);
            if (request.PageSize < 1 || request.PageSize > limits.MaximumPageSize)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.InvalidRequest));
            }

            if (!loadedContext.VerifyFreshness(cancellationToken))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.StaleContext));
            }

            if (binding.FailureReason is { } bindingFailure)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    bindingFailure));
            }

            var snapshot = BuildSnapshot(started, cancellationToken);
            Check(DocumentationScribeSemanticStage.Normalize, started, cancellationToken);

            var coreBytes = EstimateCoreBytes(snapshot.Core);
            if (coreBytes > limits.MaximumResultUtf8Bytes)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.BudgetExhausted,
                    DocumentationScribeSemanticFailureReason.BudgetExhausted));
            }

            var normalizedItems = SelectOptionalItems(
                snapshot.Items,
                snapshot.Incomplete,
                coreBytes,
                out var incomplete);
            var snapshotIdentity = SnapshotIdentity(snapshot.Core, normalizedItems, incomplete);
            var normalizedRequestIdentity = Identity(
                "semantic.normalized-request.v1",
                scribeRequest.ArtifactSha256,
                limits.MaximumOptionalItems.ToString(CultureInfo.InvariantCulture),
                limits.MaximumResultUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                limits.MaximumElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                DocumentationScribeSemanticToolSelection.SelectionId,
                DocumentationScribeSemanticToolSelection.VocabularyId,
                DocumentationScribeSemanticToolSelection.OrderingId,
                snapshotIdentity);
            var x1Commitments = DocumentationScribeContextValidation.ComputeCommitmentsSha256(
                loadedContext.Facts.Instructions.Select(item => item.Commitment)
                    .Concat(loadedContext.Facts.Evidence.Select(item => item.Commitment)));
            var scope = DocumentationScribeContextValidation.CreateCursorScope(
                DocumentationScribeSemanticToolSelection.OperationId,
                normalizedRequestIdentity,
                loadedContext.Facts.RepositoryContextRef,
                loadedContext.Facts.SymbolRef,
                DocumentationScribeSemanticToolSelection.OrderingId,
                request.PageSize,
                x1Commitments);

            var position = 0;
            if (request.Cursor is { } cursor
                && !loadedContext.TryValidateCursor(
                    cursor,
                    scope,
                    out position,
                    cancellationToken))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.InvalidCursor));
            }

            if (position < 0 || position > normalizedItems.Length)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.InvalidCursor));
            }

            Check(DocumentationScribeSemanticStage.Page, started, cancellationToken);
            var count = Math.Min(request.PageSize, normalizedItems.Length - position);
            var pageItems = normalizedItems.AsSpan(position, count).ToArray().ToImmutableArray();
            var hasMore = position + count < normalizedItems.Length;

            Check(DocumentationScribeSemanticStage.FinalFreshness, started, cancellationToken);
            ValidateFinalSources(snapshot.Validations, cancellationToken);
            if (!loadedContext.VerifyFreshness(cancellationToken))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.StaleContext));
            }

            Check(DocumentationScribeSemanticStage.Cursor, started, cancellationToken);
            var nextCursor = loadedContext.IssueCursor(
                scope,
                request.Cursor,
                count,
                hasMore,
                cancellationToken);

            ValidateFinalSources(snapshot.Validations, cancellationToken);
            if (!loadedContext.VerifyFreshness(cancellationToken))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.StaleContext));
            }

            Check(DocumentationScribeSemanticStage.Publish, started, cancellationToken);
            var outcome = incomplete.IsEmpty
                ? DocumentationScribeToolOutcome.Complete
                : DocumentationScribeToolOutcome.Incomplete;
            return ValueTask.FromResult(new DocumentationScribeSemanticToolResult(
                outcome,
                new DocumentationScribeSemanticEvidencePage(
                    snapshot.Core,
                    pageItems,
                    incomplete,
                    nextCursor),
                null));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.Cancelled,
                DocumentationScribeSemanticFailureReason.Cancelled));
        }
        catch (SemanticTimeoutException)
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.TimedOut,
                DocumentationScribeSemanticFailureReason.TimedOut));
        }
        catch (SemanticBudgetException)
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.BudgetExhausted,
                DocumentationScribeSemanticFailureReason.BudgetExhausted));
        }
        catch (SemanticIdentityCollisionException)
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.Failure,
                DocumentationScribeSemanticFailureReason.IdentityCollision));
        }
        catch (DocumentationScribeContextReadException exception)
        {
            var reason = exception.Failure == DocumentationScribeContextReadFailure.Unsafe
                ? DocumentationScribeSemanticFailureReason.UnsafeSource
                : DocumentationScribeSemanticFailureReason.SourceDrift;
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.Failure,
                reason));
        }
        catch (DecoderFallbackException)
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.Failure,
                DocumentationScribeSemanticFailureReason.SourceDrift));
        }
        catch
        {
            return ValueTask.FromResult(Terminal(
                DocumentationScribeToolOutcome.Failure,
                DocumentationScribeSemanticFailureReason.InternalFailure));
        }
    }

    public override string ToString() =>
        $"{nameof(DocumentationScribeSemanticToolPort)} {{ Operation = {DocumentationScribeSemanticToolSelection.OperationId}, Context = <bound>, Request = <bound> }}";

    private Snapshot BuildSnapshot(long started, CancellationToken cancellationToken)
    {
        var method = binding.Method!;
        var target = binding.Target!;
        Check(DocumentationScribeSemanticStage.Target, started, cancellationToken);
        var summary = CreateMethodSummary(method, target);
        var selectedObservation = ObserveSelectedTarget(cancellationToken);
        var observation = selectedObservation.ObservationSet!.Observations.Single(item =>
            item.Subject.ParentSymbolRef == loadedContext.Facts.SymbolRef
            && item.Subject.ComponentKind is null);
        var documentationState = new DocumentationScribeSemanticDocumentationState(
            observation.Value,
            observation.Completeness,
            observation.UnavailableCause);
        var declarationFact = loadedContext.Facts.Evidence.Single(item =>
            string.Equals(item.KindId, "source.target-declaration", StringComparison.Ordinal));
        var declaration = ToSourceView(declarationFact);
        var coreContentIdentity = Identity(
            "semantic.target-core.v1",
            loadedContext.Facts.ContentIdentity,
            CanonicalMethod(summary),
            CanonicalComponents(binding.ApplicableComponents),
            CanonicalDocumentation(documentationState),
            CanonicalSource(declaration));
        var correlationIdentity = Identity(
            "semantic.correlation.v1",
            loadedContext.Facts.RepositoryContextRef.Value,
            loadedContext.Facts.CompilationContextRef,
            scribeRequest.ArtifactSha256,
            coreContentIdentity);
        var core = new DocumentationScribeSemanticTargetCore(
            coreContentIdentity,
            correlationIdentity,
            summary,
            binding.ApplicableComponents,
            documentationState,
            declaration);

        var items = new List<CandidateItem>();
        var incomplete = new List<DocumentationScribeSemanticIncomplete>();
        var validations = new List<SourceValidation>();

        Check(DocumentationScribeSemanticStage.Documentation, started, cancellationToken);
        AddDocumentationItems(
            observation,
            items,
            incomplete,
            validations,
            started,
            cancellationToken);
        Check(DocumentationScribeSemanticStage.Relations, started, cancellationToken);
        AddRelationItems(
            items,
            incomplete,
            validations,
            started,
            cancellationToken);
        Check(DocumentationScribeSemanticStage.Usages, started, cancellationToken);
        try
        {
            AddUsageItems(
                items,
                incomplete,
                validations,
                started,
                cancellationToken);
        }
        catch (SemanticTraversalCompleteException)
        {
            // The closed node limit already contributed explicit incomplete metadata.
        }

        return new Snapshot(
            core,
            NormalizeCandidates(items),
            incomplete.ToImmutableArray(),
            validations.DistinctBy(item => item.Key, StringComparer.Ordinal).ToImmutableArray());
    }

    private ObservedRepositorySession ObserveSelectedTarget(CancellationToken cancellationToken)
    {
        var set = binding.ClassificationSet!;
        var target = binding.Target!;
        var buffer = new ClassificationCandidateBuffer();
        var targetLocators = AuthoritativeReferences(binding.Method!)
            .Where(reference => binding.Project!.SourceTrees.ContainsKey(reference.SyntaxTree))
            .Select(reference => CandidateLocator(
                binding.Project!,
                binding.Project!.SourceTrees[reference.SyntaxTree],
                reference.Span))
            .ToImmutableArray();
        buffer.AddTarget(
            target.SymbolRef.CompilationContextRef,
            target.SymbolRef.DocumentationCommentId,
            target.PrimaryKind,
            target.Traits,
            target.Origin,
            targetLocators);
        foreach (var component in set.Components.Where(item =>
                     item.ParentSymbolRef == loadedContext.Facts.SymbolRef))
        {
            buffer.AddComponent(
                component.ParentSymbolRef.CompilationContextRef,
                component.ParentSymbolRef.DocumentationCommentId,
                component.ComponentKind,
                component.Identity,
                component.Origin);
        }

        var selectedOutcome = buffer.Normalize(set.TargetProfile, cancellationToken: cancellationToken);
        if (selectedOutcome.Status != ClassificationRunStatus.Success)
        {
            throw new InvalidOperationException("semantic.documentation.selection-failed");
        }

        var session = ClassifiedRepositorySession.Bind(
            loadedContext.RepositorySession,
            selectedOutcome);
        var observed = new DocumentationObserver().Observe(session, cancellationToken);
        if (observed.Status != DocumentationObservationRunStatus.Success)
        {
            throw new InvalidOperationException("semantic.documentation.unavailable");
        }

        return observed;
    }

    private void AddDocumentationItems(
        DocumentationObservation observation,
        List<CandidateItem> items,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        long started,
        CancellationToken cancellationToken)
    {
        foreach (var declaration in observation.Declarations
                     .Where(item => item.DocumentationSpan is not null)
                     .OrderBy(item => SourceSortKey(item.Source), StringComparer.Ordinal)
                     .ThenBy(item => item.DocumentationSpan!.Value.Start))
        {
            Check(started, cancellationToken);
            var materialized = Materialize(
                declaration.Source,
                declaration.DocumentationSpan!.Value,
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "semantic.documentation",
                incomplete,
                validations,
                cancellationToken);
            if (materialized is null)
            {
                continue;
            }

            items.Add(CandidateItem.Create(
                priority: 0,
                materialized,
                DocumentationScribeSemanticEvidenceKind.Documentation,
                null,
                null,
                null,
                null,
                identity));
        }
    }

    private void AddRelationItems(
        List<CandidateItem> items,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        long started,
        CancellationToken cancellationToken)
    {
        foreach (var relation in binding.ClassificationSet!.Relations
                     .Where(item => item.SourceSymbolRef == loadedContext.Facts.SymbolRef
                         || item.TargetSymbolRef == loadedContext.Facts.SymbolRef)
                     .OrderBy(item => item.RelationKind)
                     .ThenBy(item => item.SourceSymbolRef.CompilationContextRef, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceSymbolRef.DocumentationCommentId, StringComparer.Ordinal)
                     .ThenBy(item => item.TargetSymbolRef.CompilationContextRef, StringComparer.Ordinal)
                     .ThenBy(item => item.TargetSymbolRef.DocumentationCommentId, StringComparer.Ordinal))
        {
            Check(started, cancellationToken);
            var outgoing = relation.SourceSymbolRef == loadedContext.Facts.SymbolRef;
            var anchorSymbol = outgoing
                ? loadedContext.Facts.SymbolRef
                : relation.SourceSymbolRef;
            var related = outgoing ? relation.TargetSymbolRef : relation.SourceSymbolRef;
            var anchor = ResolveAuthoritativeAnchor(anchorSymbol, cancellationToken);
            if (anchor is null)
            {
                AddIncomplete(
                    incomplete,
                    DocumentationScribeSemanticIncompleteReason.RelationSourceUnavailable,
                    1);
                continue;
            }

            var materialized = Materialize(
                anchor.Value.Project,
                anchor.Value.Tree,
                anchor.Value.Source,
                anchor.Value.Span,
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "semantic.relation",
                incomplete,
                validations,
                cancellationToken);
            if (materialized is null)
            {
                AddIncomplete(
                    incomplete,
                    DocumentationScribeSemanticIncompleteReason.RelationSourceUnavailable,
                    1);
                continue;
            }

            items.Add(CandidateItem.Create(
                priority: 1,
                materialized,
                DocumentationScribeSemanticEvidenceKind.Relation,
                null,
                relation.RelationKind,
                outgoing
                    ? DocumentationScribeSemanticRelationDirection.Outgoing
                    : DocumentationScribeSemanticRelationDirection.Incoming,
                related,
                identity));
        }
    }

    private void AddUsageItems(
        List<CandidateItem> items,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        long started,
        CancellationToken cancellationToken)
    {
        var projects = loadedContext.RepositorySession.Projects
            .OrderBy(item => item.CompilationContextRef, StringComparer.Ordinal)
            .ToArray();
        if (projects.Length > limits.MaximumCompilations)
        {
            AddIncomplete(
                incomplete,
                DocumentationScribeSemanticIncompleteReason.CompilationLimit,
                projects.Length - limits.MaximumCompilations);
            projects = projects[..limits.MaximumCompilations];
        }

        var sourceTreeCount = 0;
        var syntaxNodeCount = 0;
        var occurrences = new Dictionary<string, Occurrence>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            Check(started, cancellationToken);
            foreach (var pair in project.SourceTrees
                         .OrderBy(item => SourceSortKey(item.Value), StringComparer.Ordinal))
            {
                Check(started, cancellationToken);
                if (++sourceTreeCount > limits.MaximumSourceTrees)
                {
                    AddIncomplete(
                        incomplete,
                        DocumentationScribeSemanticIncompleteReason.SourceTreeLimit,
                        1);
                    return;
                }

                var root = pair.Key.GetRoot(cancellationToken);
                var model = project.Compilation.GetSemanticModel(pair.Key, ignoreAccessibility: true);
                var consumed = new List<TextSpan>();

                foreach (var nameOf in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                             .Where(IsNameOfExpression)
                             .OrderBy(node => node.SpanStart))
                {
                    CountNode(ref syntaxNodeCount, incomplete);
                    Check(started, cancellationToken);
                    if (!nameOf.ArgumentList.DescendantNodesAndSelf()
                            .Any(node => ResolvesToTarget(model.GetSymbolInfo(node, cancellationToken).Symbol)))
                    {
                        continue;
                    }

                    AddOccurrence(
                        occurrences,
                        project,
                        pair.Key,
                        pair.Value,
                        nameOf.Span,
                        DocumentationScribeSemanticUsageKind.NameOf,
                        isTest: false);
                    consumed.Add(nameOf.Span);
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                             .Where(node => !IsNameOfExpression(node))
                             .OrderBy(node => node.SpanStart))
                {
                    CountNode(ref syntaxNodeCount, incomplete);
                    Check(started, cancellationToken);
                    if (consumed.Any(span => span.Contains(invocation.Span))
                        || !ResolvesToTarget(model.GetSymbolInfo(invocation, cancellationToken).Symbol))
                    {
                        continue;
                    }

                    var isTest = IsTestOccurrence(model, invocation.SpanStart, cancellationToken);
                    AddOccurrence(
                        occurrences,
                        project,
                        pair.Key,
                        pair.Value,
                        invocation.Span,
                        DocumentationScribeSemanticUsageKind.Invocation,
                        isTest);
                    consumed.Add(invocation.Span);
                }

                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>()
                             .OrderBy(node => node.SpanStart))
                {
                    CountNode(ref syntaxNodeCount, incomplete);
                    Check(started, cancellationToken);
                    if (consumed.Any(span => span.Contains(name.Span))
                        || IsDeclarationName(name)
                        || !ResolvesToTarget(model.GetSymbolInfo(name, cancellationToken).Symbol))
                    {
                        continue;
                    }

                    AddOccurrence(
                        occurrences,
                        project,
                        pair.Key,
                        pair.Value,
                        name.Span,
                        DocumentationScribeSemanticUsageKind.MemberReference,
                        IsTestOccurrence(model, name.SpanStart, cancellationToken));
                }
            }
        }

        foreach (var occurrence in occurrences.Values
                     .OrderBy(item => item.IsTest ? 0 : 1)
                     .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            Check(started, cancellationToken);
            var authority = occurrence.IsTest
                ? DocumentationScribeContextAuthority.Test
                : DocumentationScribeContextAuthority.Usage;
            var role = occurrence.IsTest
                ? DocumentationScribeContextRole.TestEvidence
                : DocumentationScribeContextRole.UsageEvidence;
            var materialized = Materialize(
                occurrence.Project,
                occurrence.Tree,
                occurrence.Source,
                occurrence.Span,
                authority,
                role,
                "semantic.usage." + occurrence.Kind.ToString().ToLowerInvariant(),
                incomplete,
                validations,
                cancellationToken);
            if (materialized is null)
            {
                continue;
            }

            items.Add(CandidateItem.Create(
                occurrence.IsTest ? 2 : 3,
                materialized,
                occurrence.IsTest
                    ? DocumentationScribeSemanticEvidenceKind.TestUsage
                    : DocumentationScribeSemanticEvidenceKind.Usage,
                occurrence.Kind,
                null,
                null,
                null,
                identity));
        }
    }

    private void CountNode(
        ref int syntaxNodeCount,
        List<DocumentationScribeSemanticIncomplete> incomplete)
    {
        if (++syntaxNodeCount <= limits.MaximumSyntaxNodes)
        {
            return;
        }

        AddIncomplete(
            incomplete,
            DocumentationScribeSemanticIncompleteReason.SyntaxNodeLimit,
            1);
        throw new SemanticTraversalCompleteException();
    }

    private void AddOccurrence(
        Dictionary<string, Occurrence> occurrences,
        LoadedProject project,
        SyntaxTree tree,
        LoadedSourceTree source,
        TextSpan span,
        DocumentationScribeSemanticUsageKind kind,
        bool isTest)
    {
        var key = string.Join(
            "|",
            project.CompilationContextRef,
            SourceSortKey(source),
            span.Start.ToString(CultureInfo.InvariantCulture),
            span.End.ToString(CultureInfo.InvariantCulture),
            kind.ToString(),
            loadedContext.Facts.SymbolRef.CompilationContextRef,
            loadedContext.Facts.SymbolRef.DocumentationCommentId);
        occurrences.TryAdd(key, new Occurrence(
            key,
            project,
            tree,
            source,
            span,
            kind,
            isTest));
    }

    private Materialized? Materialize(
        DocumentationSourceIdentity source,
        Utf16Span span,
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string kindId,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        CancellationToken cancellationToken)
    {
        var project = loadedContext.RepositorySession.Projects.SingleOrDefault(item =>
            string.Equals(item.ProjectIdentity, source.ProjectIdentity, StringComparison.Ordinal));
        if (project is null)
        {
            return null;
        }

        var pair = project.SourceTrees.SingleOrDefault(item => SourceMatches(item.Value, source));
        return pair.Key is null
            ? null
            : Materialize(
                project,
                pair.Key,
                pair.Value,
                new TextSpan(span.Start, span.End - span.Start),
                authority,
                role,
                kindId,
                incomplete,
                validations,
                cancellationToken);
    }

    private Materialized? Materialize(
        LoadedProject project,
        SyntaxTree tree,
        LoadedSourceTree source,
        TextSpan span,
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string kindId,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        CancellationToken cancellationToken)
    {
        var text = tree.GetText(cancellationToken).ToString();
        if (span.Start < 0 || span.End > text.Length || span.Length <= 0)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Stale,
                "semantic.stale.range");
        }

        var content = text[span.Start..span.End];
        if (StrictUtf8.GetByteCount(content) > limits.MaximumIncludedSourceUtf8Bytes)
        {
            AddIncomplete(
                incomplete,
                DocumentationScribeSemanticIncompleteReason.SourceByteLimit,
                1);
            return null;
        }

        DocumentationScribeContextSourceCommitment commitment;
        SourceValidation? validation = null;
        EvidenceLocator locator;
        if (source.Kind == LoadedSourceKind.Repository)
        {
            if (source.RepositoryPath is null || source.PhysicalSourceIdentity is null)
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Unsafe,
                    "semantic.unsafe.source-binding");
            }

            RepositorySourceValidation? repository;
            try
            {
                repository = ReadRepositorySource(
                    source.RepositoryPath,
                    source.PhysicalSourceIdentity,
                    text,
                    cancellationToken);
            }
            catch (SemanticBudgetException)
            {
                AddIncomplete(
                    incomplete,
                    DocumentationScribeSemanticIncompleteReason.SourceByteLimit,
                    1);
                return null;
            }
            if (repository is null)
            {
                AddIncomplete(
                    incomplete,
                    DocumentationScribeSemanticIncompleteReason.UnsupportedEncoding,
                    1);
                return null;
            }

            validation = repository;
            validations.Add(repository);
            locator = EvidenceInput.RepositoryLocator(source.RepositoryPath, span.Start, span.End);
            var includedBytes = StrictUtf8.GetBytes(content);
            commitment = DocumentationScribeContextValidation.CreateEvidenceSourceCommitment(
                locator,
                repository.SourceSha256,
                Sha256(includedBytes),
                repository.Bytes.Length,
                includedBytes.Length,
                includedBytes.Length < repository.Bytes.Length,
                repository.HasUtf8Bom,
                false);
        }
        else
        {
            var fact = source.GeneratedSource
                ?? throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.generated-fact");
            if (!string.Equals(fact.CompilationContextRef, project.CompilationContextRef, StringComparison.Ordinal)
                || !string.Equals(fact.ProjectIdentity, project.ProjectIdentity, StringComparison.Ordinal)
                || !string.Equals(fact.SourceText, text, StringComparison.Ordinal)
                || !string.Equals(Sha256(StrictUtf8.GetBytes(text)), fact.SourceSha256, StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.generated-source");
            }

            validation = new GeneratedSourceValidation(
                project.CompilationContextRef,
                tree,
                source,
                text,
                fact.SourceSha256);
            validations.Add(validation);

            var generatedKind = source.Kind == LoadedSourceKind.SourceGenerator
                ? GeneratedOutputKind.SourceGenerator
                : GeneratedOutputKind.ToolGenerated;
            locator = EvidenceInput.GeneratedOutputLocator(
                generatedKind,
                fact.ProducerId,
                fact.OutputId,
                fact.SourceSha256,
                span.Start,
                span.End);
            var fullBytes = StrictUtf8.GetBytes(text);
            var includedBytes = StrictUtf8.GetBytes(content);
            commitment = DocumentationScribeContextValidation.CreateEvidenceSourceCommitment(
                locator,
                fact.SourceSha256,
                Sha256(includedBytes),
                fullBytes.Length,
                includedBytes.Length,
                includedBytes.Length < fullBytes.Length,
                false,
                false);
        }

        var evidence = DocumentationScribeContextValidation.CreateEvidenceFact(
            authority,
            role,
            "symbol." + DocumentationScribeContextValidation.ComputeSymbolRefSha256(
                loadedContext.Facts.SymbolRef),
            kindId,
            commitment,
            content,
            span.Start,
            span.End,
            span.Start,
            span.End);
        return new Materialized(evidence, ToSourceView(evidence), validation);
    }

    private RepositorySourceValidation? ReadRepositorySource(
        string repositoryPath,
        string expectedPhysicalIdentity,
        string expectedText,
        CancellationToken cancellationToken)
    {
        var resolver = new RepositoryPathResolver();
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        var fullPath = Path.GetFullPath(Path.Join(
            root,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        var resolved = resolver.ResolveSource(root, fullPath);
        var physicalIdentity = resolver.PhysicalIdentity(root, resolved.PhysicalPath);
        if (!string.Equals(physicalIdentity, expectedPhysicalIdentity, StringComparison.Ordinal))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "semantic.unsafe.physical-binding");
        }

        var rootIdentity = DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root);
        var directories = ParentDirectories(root, resolved.PhysicalPath)
            .Select(path => new DirectoryValidation(
                path,
                DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(path)))
            .ToImmutableArray();
        DocumentationScribeContextStableRead read;
        try
        {
            read = DocumentationScribeContextStableFileReader.ReadRegularFile(
                resolved.PhysicalPath,
                limits.MaximumSourceFileUtf8Bytes,
                cancellationToken);
        }
        catch (DocumentationScribeContextReadException exception)
            when (exception.Failure == DocumentationScribeContextReadFailure.Budget)
        {
            throw new SemanticBudgetException();
        }

        if (read.Identity.LinkCount != 1)
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "semantic.unsafe.link-count");
        }

        bool hasBom;
        string decoded;
        try
        {
            hasBom = HasUtf8Bom(read.Bytes);
            decoded = StrictUtf8.GetString(hasBom ? read.Bytes.AsSpan(3) : read.Bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (!string.Equals(decoded, expectedText, StringComparison.Ordinal))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Stale,
                "semantic.stale.source-text");
        }

        return new RepositorySourceValidation(
            repositoryPath,
            resolved.PhysicalPath,
            expectedPhysicalIdentity,
            expectedText,
            root,
            rootIdentity,
            directories,
            read.Identity,
            read.Bytes,
            Sha256(read.Bytes),
            hasBom);
    }

    private void ValidateFinalSources(
        ImmutableArray<SourceValidation> validations,
        CancellationToken cancellationToken)
    {
        foreach (var validation in validations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (validation is GeneratedSourceValidation generated)
            {
                ValidateGeneratedSource(generated, cancellationToken);
                continue;
            }

            var repository = (RepositorySourceValidation)validation;
            if (DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(repository.Root)
                    != repository.RootIdentity
                || repository.Directories.Any(item =>
                    DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(item.Path)
                    != item.Identity))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.directory");
            }

            var resolver = new RepositoryPathResolver();
            var resolved = resolver.ResolveSource(repository.Root, repository.PhysicalPath);
            if (!string.Equals(
                    resolver.PhysicalIdentity(repository.Root, resolved.PhysicalPath),
                    repository.ExpectedPhysicalIdentity,
                    StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Unsafe,
                    "semantic.unsafe.physical-binding");
            }

            var read = DocumentationScribeContextStableFileReader.ReadRegularFile(
                resolved.PhysicalPath,
                limits.MaximumSourceFileUtf8Bytes,
                cancellationToken);
            if (read.Identity != repository.FileIdentity
                || read.Identity.LinkCount != 1
                || !read.Bytes.AsSpan().SequenceEqual(repository.Bytes))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.source-bytes");
            }

            var decoded = StrictUtf8.GetString(
                repository.HasUtf8Bom ? read.Bytes.AsSpan(3) : read.Bytes);
            if (!string.Equals(decoded, repository.ExpectedText, StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.source-text");
            }
        }
    }

    private void ValidateGeneratedSource(
        GeneratedSourceValidation validation,
        CancellationToken cancellationToken)
    {
        var repository = GetClassifiedSession(loadedContext).RepositorySession;
        var project = repository.Projects.SingleOrDefault(item =>
            string.Equals(
                item.CompilationContextRef,
                validation.CompilationContextRef,
                StringComparison.Ordinal));
        if (project is null
            || !project.SourceTrees.TryGetValue(validation.Tree, out var current)
            || current != validation.Source
            || !string.Equals(
                validation.Tree.GetText(cancellationToken).ToString(),
                validation.ExpectedText,
                StringComparison.Ordinal)
            || !string.Equals(
                Sha256(StrictUtf8.GetBytes(validation.ExpectedText)),
                validation.SourceSha256,
                StringComparison.Ordinal))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Stale,
                "semantic.stale.generated-source");
        }
    }

    private ImmutableArray<DocumentationScribeSemanticEvidenceItem> NormalizeCandidates(
        List<CandidateItem> candidates)
    {
        var identities = new Dictionary<string, CandidateItem>(StringComparer.Ordinal);
        foreach (var candidate in candidates
                     .OrderBy(item => item.Priority)
                     .ThenBy(item => item.SortKey, StringComparer.Ordinal))
        {
            if (identities.TryGetValue(candidate.Item.ItemIdentity, out var existing))
            {
                if (!string.Equals(
                    existing.CanonicalMaterial,
                    candidate.CanonicalMaterial,
                    StringComparison.Ordinal))
                {
                    throw new SemanticIdentityCollisionException();
                }

                continue;
            }

            identities.Add(candidate.Item.ItemIdentity, candidate);
        }

        return identities.Values
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.SortKey, StringComparer.Ordinal)
            .Select(item => item.Item)
            .ToImmutableArray();
    }

    private ImmutableArray<DocumentationScribeSemanticEvidenceItem> SelectOptionalItems(
        ImmutableArray<DocumentationScribeSemanticEvidenceItem> items,
        ImmutableArray<DocumentationScribeSemanticIncomplete> initialIncomplete,
        int coreBytes,
        out ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete)
    {
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeSemanticEvidenceItem>();
        var incompleteBuilder = initialIncomplete.ToBuilder();
        var bytes = coreBytes;
        foreach (var item in items)
        {
            if (builder.Count >= limits.MaximumOptionalItems)
            {
                AddIncomplete(
                    incompleteBuilder,
                    DocumentationScribeSemanticIncompleteReason.ItemLimit,
                    items.Length - builder.Count);
                break;
            }

            var itemBytes = EstimateItemBytes(item);
            if (bytes + itemBytes > limits.MaximumResultUtf8Bytes)
            {
                AddIncomplete(
                    incompleteBuilder,
                    DocumentationScribeSemanticIncompleteReason.ResultByteLimit,
                    items.Length - builder.Count);
                break;
            }

            builder.Add(item);
            bytes += itemBytes;
        }

        incomplete = incompleteBuilder
            .GroupBy(item => item.Reason)
            .OrderBy(group => group.Key)
            .Select(group => new DocumentationScribeSemanticIncomplete(
                group.Key,
                group.Sum(item => item.OmittedCount)))
            .ToImmutableArray();
        return builder.ToImmutable();
    }

    private Anchor? ResolveAuthoritativeAnchor(
        SymbolRef symbolRef,
        CancellationToken cancellationToken)
    {
        var project = loadedContext.RepositorySession.Projects.SingleOrDefault(item =>
            string.Equals(item.CompilationContextRef, symbolRef.CompilationContextRef, StringComparison.Ordinal));
        if (project is null)
        {
            return null;
        }

        var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(
                symbolRef.DocumentationCommentId,
                project.Compilation)
            .Select(CanonicalPartialSymbol)
            .Distinct(SymbolEqualityComparer.Default)
            .ToArray();
        if (symbols.Length != 1)
        {
            return null;
        }

        return AuthoritativeReferences(symbols[0])
            .Where(reference => project.SourceTrees.ContainsKey(reference.SyntaxTree))
            .Select(reference => new Anchor(
                project,
                reference.SyntaxTree,
                project.SourceTrees[reference.SyntaxTree],
                reference.Span))
            .OrderBy(item => SourceSortKey(item.Source), StringComparer.Ordinal)
            .ThenBy(item => item.Span.Start)
            .FirstOrDefault();
    }

    private bool ResolvesToTarget(ISymbol? symbol)
    {
        symbol = symbol is IAliasSymbol alias ? alias.Target : symbol;
        if (symbol is not IMethodSymbol method)
        {
            return false;
        }

        method = NormalizeMethod(method);
        return string.Equals(
                method.GetDocumentationCommentId(),
                loadedContext.Facts.SymbolRef.DocumentationCommentId,
                StringComparison.Ordinal)
            && string.Equals(
                method.ContainingAssembly?.Identity.Name,
                binding.Method!.ContainingAssembly.Identity.Name,
                StringComparison.Ordinal);
    }

    private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
    {
        method = method.ReducedFrom ?? method;
        method = method.OriginalDefinition;
        return method.PartialDefinitionPart ?? method;
    }

    private bool IsTestOccurrence(
        SemanticModel model,
        int position,
        CancellationToken cancellationToken)
    {
        if (model.GetEnclosingSymbol(position, cancellationToken) is not IMethodSymbol method)
        {
            return false;
        }

        return method.GetAttributes().Any(attribute =>
        {
            var type = attribute.AttributeClass;
            if (type is null)
            {
                return false;
            }

            var metadataName = FullMetadataName(type);
            var assemblyName = type.ContainingAssembly?.Identity.Name;
            return DocumentationScribeSemanticToolSelection.TestMarkers.Any(marker =>
                string.Equals(marker.AttributeMetadataName, metadataName, StringComparison.Ordinal)
                && string.Equals(marker.AssemblySimpleName, assemblyName, StringComparison.Ordinal));
        });
    }

    private DocumentationScribeSemanticMethodSummary CreateMethodSummary(
        IMethodSymbol method,
        TargetClassification target)
    {
        var containingTypeId = method.ContainingType.GetDocumentationCommentId();
        if (containingTypeId is null)
        {
            throw new UnsupportedSignatureException();
        }

        var containingTypeRef = EvidenceInput.TargetSubject(
            loadedContext.Facts.CompilationContextRef,
            containingTypeId).ParentSymbolRef;
        var parameters = method.Parameters
            .OrderBy(item => item.Ordinal)
            .Select(item => new DocumentationScribeSemanticParameterFact(
                item.Ordinal,
                item.Name,
                CreateType(item.Type, 0),
                RefKind(item.RefKind),
                item.IsParams,
                item.IsOptional))
            .ToImmutableArray();
        var typeParameters = method.TypeParameters
            .OrderBy(item => item.Ordinal)
            .Select(item => new DocumentationScribeSemanticTypeParameterFact(
                item.Ordinal,
                item.Name,
                item.HasReferenceTypeConstraint,
                item.HasValueTypeConstraint,
                item.HasUnmanagedTypeConstraint,
                item.HasNotNullConstraint,
                item.HasConstructorConstraint,
                Nullability(item.ReferenceTypeConstraintNullableAnnotation),
                item.ConstraintTypes.Select(type => CreateType(type, 0)).ToImmutableArray()))
            .ToImmutableArray();
        return new DocumentationScribeSemanticMethodSummary(
            loadedContext.Facts.SymbolRef,
            target.Traits.Order().ToImmutableArray(),
            target.Origin,
            method.MetadataName,
            method.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : method.ContainingNamespace.ToDisplayString(),
            containingTypeRef,
            Accessibility(method.DeclaredAccessibility),
            EffectiveAccessibility(method),
            method.ReturnsByRefReadonly
                ? DocumentationScribeSemanticRefKind.RefReadOnly
                : method.ReturnsByRef
                    ? DocumentationScribeSemanticRefKind.Ref
                    : DocumentationScribeSemanticRefKind.None,
            CreateType(method.ReturnType, 0),
            parameters,
            typeParameters);
    }

    private DocumentationScribeSemanticTypeFact CreateType(ITypeSymbol type, int depth)
    {
        if (depth > 16 || type.TypeKind is TypeKind.Error or TypeKind.FunctionPointer)
        {
            throw new UnsupportedSignatureException();
        }

        var nullability = Nullability(type.NullableAnnotation);
        return type switch
        {
            IDynamicTypeSymbol => new(
                DocumentationScribeSemanticTypeKind.Dynamic,
                nullability,
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null),
            IArrayTypeSymbol array => new(
                DocumentationScribeSemanticTypeKind.Array,
                nullability,
                null,
                null,
                [],
                CreateType(array.ElementType, depth + 1),
                array.Rank,
                null,
                null,
                null),
            IPointerTypeSymbol pointer => new(
                DocumentationScribeSemanticTypeKind.Pointer,
                nullability,
                null,
                null,
                [],
                CreateType(pointer.PointedAtType, depth + 1),
                null,
                null,
                null,
                null),
            ITypeParameterSymbol parameter => new(
                DocumentationScribeSemanticTypeKind.TypeParameter,
                nullability,
                null,
                null,
                [],
                null,
                null,
                parameter.DeclaringMethod is null
                    ? DocumentationScribeSemanticTypeParameterOwner.Type
                    : DocumentationScribeSemanticTypeParameterOwner.Method,
                parameter.Ordinal,
                parameter.Name),
            INamedTypeSymbol named => new(
                DocumentationScribeSemanticTypeKind.Named,
                nullability,
                named.ContainingAssembly?.Identity.Name,
                FullMetadataName(named.OriginalDefinition),
                named.TypeArguments.Select(item => CreateType(item, depth + 1)).ToImmutableArray(),
                null,
                null,
                null,
                null,
                null),
            _ => throw new UnsupportedSignatureException(),
        };
    }

    private static Binding Bind(
        DocumentationScribeLoadedContext context,
        DocumentationScribeRequest request)
    {
        var set = GetClassificationSet(context);
        if (set is null)
        {
            return Binding.Failed(DocumentationScribeSemanticFailureReason.StaleContext);
        }

        var targets = set.Targets.Where(item => item.SymbolRef == context.Facts.SymbolRef).ToArray();
        if (targets.Length != 1)
        {
            return Binding.Failed(DocumentationScribeSemanticFailureReason.AmbiguousSymbol, set);
        }

        var target = targets[0];
        if (target.PrimaryKind != PrimarySymbolKind.Method)
        {
            return Binding.Failed(
                DocumentationScribeSemanticFailureReason.UnsupportedTargetKind,
                set,
                target);
        }

        if (target.SupportStatus != SupportStatus.Supported)
        {
            return Binding.Failed(
                DocumentationScribeSemanticFailureReason.UnsupportedTargetStatus,
                set,
                target);
        }

        var project = context.RepositorySession.Projects.SingleOrDefault(item =>
            string.Equals(item.CompilationContextRef, context.Facts.CompilationContextRef, StringComparison.Ordinal));
        if (project is null)
        {
            return Binding.Failed(DocumentationScribeSemanticFailureReason.AmbiguousSymbol, set, target);
        }

        var methods = DocumentationCommentId.GetSymbolsForDeclarationId(
                context.Facts.SymbolRef.DocumentationCommentId,
                project.Compilation)
            .OfType<IMethodSymbol>()
            .Select(NormalizeMethod)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        if (methods.Length != 1)
        {
            return Binding.Failed(DocumentationScribeSemanticFailureReason.AmbiguousSymbol, set, target);
        }

        var method = methods[0];
        try
        {
            _ = ValidateRepresentableMethod(method);
        }
        catch (UnsupportedSignatureException)
        {
            return Binding.Failed(
                DocumentationScribeSemanticFailureReason.UnsupportedSignature,
                set,
                target,
                method);
        }

        var components = ExpectedApplicableComponents(set, context.Facts.SymbolRef, method);
        return new Binding(set, target, project, method, components, null);
    }

    private static ClassificationSet? GetClassificationSet(DocumentationScribeLoadedContext context)
        => GetClassifiedSession(context).Classification.ClassificationSet;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "classifiedSession")]
    private static extern ref readonly ClassifiedRepositorySession GetClassifiedSession(
        DocumentationScribeLoadedContext context);

    private static bool ValidateRepresentableMethod(IMethodSymbol method)
    {
        static void ValidateType(ITypeSymbol type, int depth)
        {
            if (depth > 16 || type.TypeKind is TypeKind.Error or TypeKind.FunctionPointer)
            {
                throw new UnsupportedSignatureException();
            }

            switch (type)
            {
                case IArrayTypeSymbol array:
                    ValidateType(array.ElementType, depth + 1);
                    break;
                case IPointerTypeSymbol pointer:
                    ValidateType(pointer.PointedAtType, depth + 1);
                    break;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                    {
                        ValidateType(argument, depth + 1);
                    }
                    break;
                case ITypeParameterSymbol:
                case IDynamicTypeSymbol:
                    break;
                default:
                    throw new UnsupportedSignatureException();
            }
        }

        ValidateType(method.ReturnType, 0);
        foreach (var parameter in method.Parameters)
        {
            ValidateType(parameter.Type, 0);
        }
        foreach (var parameter in method.TypeParameters)
        {
            foreach (var constraint in parameter.ConstraintTypes)
            {
                ValidateType(constraint, 0);
            }
        }

        return true;
    }

    private static ImmutableArray<DocumentationScribeSemanticApplicableComponent> ExpectedApplicableComponents(
        ClassificationSet set,
        SymbolRef symbolRef,
        IMethodSymbol method) =>
        set.Components
            .Where(item => item.ParentSymbolRef == symbolRef
                && item.SupportStatus == SupportStatus.Supported
                && item.ComponentKind is ComponentKind.TypeParameter
                    or ComponentKind.Parameter
                    or ComponentKind.Return
                    or ComponentKind.Value)
            .Select(item => new DocumentationScribeSemanticApplicableComponent(
                item.ComponentKind switch
                {
                    ComponentKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
                    ComponentKind.Parameter => DocumentationPatchComponentKind.Parameter,
                    ComponentKind.Return => DocumentationPatchComponentKind.Return,
                    ComponentKind.Value => DocumentationPatchComponentKind.Value,
                    _ => throw new InvalidOperationException("semantic.component.unexpected"),
                },
                item.Identity,
                ComponentName(item, method)))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToImmutableArray();

    private static string? ComponentName(ComponentClassification component, IMethodSymbol method)
    {
        if (component.ComponentKind is ComponentKind.Return or ComponentKind.Value)
        {
            return null;
        }

        var separator = component.Identity.LastIndexOf('/');
        if (separator < 0
            || !int.TryParse(
                component.Identity.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal))
        {
            return null;
        }

        return component.ComponentKind == ComponentKind.TypeParameter
            ? ordinal < method.TypeParameters.Length ? method.TypeParameters[ordinal].Name : null
            : ordinal < method.Parameters.Length ? method.Parameters[ordinal].Name : null;
    }

    private static bool ApplicableComponentsEqual(
        ImmutableArray<DocumentationScribeSemanticApplicableComponent> expected,
        ImmutableArray<DocumentationPatchApplicableComponent> actual)
    {
        var normalized = actual
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();
        return expected.Length == normalized.Length
            && expected.Zip(normalized).All(pair =>
                pair.First.Kind == pair.Second.Kind
                && string.Equals(pair.First.Identity, pair.Second.Identity, StringComparison.Ordinal)
                && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));
    }

    private DocumentationScribeSemanticSourceEvidence ToSourceView(
        DocumentationScribeEvidenceContextFact evidence)
    {
        var range = evidence.Range
            ?? throw new InvalidOperationException("semantic.source.range-missing");
        var commitment = evidence.Commitment;
        return commitment.Locator switch
        {
            RepositoryEvidenceLocator repository => new DocumentationScribeSemanticRepositoryEvidence(
                repository.Path,
                commitment.ContentSha256,
                commitment.IncludedContentSha256,
                commitment.OriginalUtf8ByteCount,
                commitment.IncludedUtf8ByteCount,
                commitment.IsTruncated,
                commitment.HasUtf8Bom,
                range,
                evidence.IncludedRange,
                evidence.Content),
            GeneratedOutputEvidenceLocator
            {
                ProducerKind: GeneratedOutputKind.SourceGenerator,
            } generated => new DocumentationScribeSemanticSourceGeneratorEvidence(
                    generated.ProducerId,
                    generated.OutputId,
                    commitment.ContentSha256,
                    commitment.IncludedContentSha256,
                    commitment.OriginalUtf8ByteCount,
                    commitment.IncludedUtf8ByteCount,
                    commitment.IsTruncated,
                    range,
                    evidence.IncludedRange,
                    evidence.Content),
            GeneratedOutputEvidenceLocator generated => new DocumentationScribeSemanticToolGeneratedEvidence(
                generated.ProducerId,
                generated.OutputId,
                commitment.ContentSha256,
                commitment.IncludedContentSha256,
                commitment.OriginalUtf8ByteCount,
                commitment.IncludedUtf8ByteCount,
                commitment.IsTruncated,
                range,
                evidence.IncludedRange,
                evidence.Content),
            _ => throw new InvalidOperationException("semantic.source.locator-not-admitted"),
        };
    }

    private static bool SourceMatches(
        LoadedSourceTree loaded,
        DocumentationSourceIdentity source) => (loaded.Kind, source) switch
        {
            (LoadedSourceKind.Repository, RepositoryDocumentationSourceIdentity repository) =>
                string.Equals(loaded.RepositoryPath, repository.Path, StringComparison.Ordinal),
            (LoadedSourceKind.SourceGenerator, GeneratedDocumentationSourceIdentity generated) =>
                generated.Kind == DocumentationSourceKind.SourceGenerator
                && loaded.GeneratedSource is { } fact
                && string.Equals(fact.ProducerId, generated.ProducerId, StringComparison.Ordinal)
                && string.Equals(fact.OutputId, generated.OutputId, StringComparison.Ordinal),
            (LoadedSourceKind.ToolGenerated, GeneratedDocumentationSourceIdentity generated) =>
                generated.Kind == DocumentationSourceKind.ToolGenerated
                && loaded.GeneratedSource is { } fact
                && string.Equals(fact.ProducerId, generated.ProducerId, StringComparison.Ordinal)
                && string.Equals(fact.OutputId, generated.OutputId, StringComparison.Ordinal),
            _ => false,
        };

    private static CandidateLocator CandidateLocator(
        LoadedProject project,
        LoadedSourceTree source,
        TextSpan span) => source.Kind switch
        {
            LoadedSourceKind.Repository => ClassificationInput.RepositoryLocator(
                source.RepositoryPath!,
                span.Start,
                span.End),
            LoadedSourceKind.SourceGenerator => ClassificationInput.GeneratedSourceLocator(
                source.GeneratedSource!.ProducerId,
                source.GeneratedSource.OutputId,
                span.Start,
                span.End),
            LoadedSourceKind.ToolGenerated => ClassificationInput.ToolGeneratedLocator(
                source.GeneratedSource!.ProducerId,
                source.GeneratedSource.OutputId,
                span.Start,
                span.End),
            _ => throw new InvalidOperationException(
                "semantic.source.kind-not-admitted:" + project.ProjectIdentity),
        };

    private static DocumentationScribeSemanticToolLimits Intersect(
        DocumentationScribeSemanticToolLimits semantic,
        DocumentationScribeRunLimits request) => new(
        semantic.MaximumPageSize,
        Math.Min(semantic.MaximumOptionalItems, Math.Max(1, request.MaximumEvidenceReferences)),
        Math.Min(semantic.MaximumResultUtf8Bytes, Math.Max(1024, request.MaximumEvidenceUtf8Bytes)),
        semantic.MaximumSourceFileUtf8Bytes,
        semantic.MaximumIncludedSourceUtf8Bytes,
        semantic.MaximumCompilations,
        semantic.MaximumSourceTrees,
        semantic.MaximumSyntaxNodes,
        Math.Min(semantic.MaximumElapsedMilliseconds, request.MaximumElapsedMilliseconds));

    private void Check(
        DocumentationScribeSemanticStage stage,
        long started,
        CancellationToken cancellationToken)
    {
        stageObserver?.Invoke(stage);
        Check(started, cancellationToken);
    }

    private void Check(long started, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > limits.MaximumElapsedMilliseconds)
        {
            throw new SemanticTimeoutException();
        }
    }

    private static DocumentationScribeSemanticToolResult Terminal(
        DocumentationScribeToolOutcome outcome,
        DocumentationScribeSemanticFailureReason reason) => new(outcome, null, reason);

    private string Identity(string domain, params string[] values) =>
        identity(Canonical(domain, values));

    private static string Canonical(string domain, IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        Append(builder, domain);
        foreach (var value in values)
        {
            Append(builder, value);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value) => builder
        .Append(StrictUtf8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
        .Append(':')
        .Append(value);

    private static string Sha256(string value) => Sha256(StrictUtf8.GetBytes(value));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool HasUtf8Bom(byte[] bytes) => bytes.Length >= 3
        && bytes[0] == 0xef
        && bytes[1] == 0xbb
        && bytes[2] == 0xbf;

    private static int EstimateCoreBytes(DocumentationScribeSemanticTargetCore core) =>
        StrictUtf8.GetByteCount(Canonical(
            "core",
            [
                core.ContentIdentity,
                core.CorrelationIdentity,
                CanonicalMethod(core.Method),
                CanonicalComponents(core.ApplicableComponents),
                CanonicalDocumentation(core.Documentation),
                CanonicalSource(core.Declaration),
            ]));

    private static int EstimateItemBytes(DocumentationScribeSemanticEvidenceItem item) =>
        StrictUtf8.GetByteCount(CanonicalItem(item));

    private string SnapshotIdentity(
        DocumentationScribeSemanticTargetCore core,
        ImmutableArray<DocumentationScribeSemanticEvidenceItem> items,
        ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete) => Identity(
            "semantic.snapshot.v1",
            core.ContentIdentity,
            string.Join("|", items.Select(CanonicalItem)),
            string.Join("|", incomplete.Select(item => $"{item.Reason}:{item.OmittedCount}")));

    private static string CanonicalMethod(DocumentationScribeSemanticMethodSummary method) => Canonical(
        "method",
        [
            method.SymbolRef.CompilationContextRef,
            method.SymbolRef.DocumentationCommentId,
            string.Join(",", method.Traits.Order()),
            method.Origin.ToString(),
            method.MetadataName,
            method.ContainingNamespace,
            method.ContainingTypeSymbolRef.DocumentationCommentId,
            method.DeclaredAccessibility.ToString(),
            method.EffectiveAccessibility.ToString(),
            method.ReturnRefKind.ToString(),
            CanonicalType(method.ReturnType),
            string.Join("|", method.Parameters.Select(item =>
                $"{item.Ordinal}:{item.Name}:{CanonicalType(item.Type)}:{item.RefKind}:{item.IsParams}:{item.IsOptional}")),
            string.Join("|", method.TypeParameters.Select(item =>
                $"{item.Ordinal}:{item.Name}:{item.HasReferenceTypeConstraint}:{item.HasValueTypeConstraint}:{item.HasUnmanagedConstraint}:{item.HasNotNullConstraint}:{item.HasConstructorConstraint}:{item.ReferenceTypeConstraintNullability}:{string.Join(",", item.ConstraintTypes.Select(CanonicalType))}")),
        ]);

    private static string CanonicalType(DocumentationScribeSemanticTypeFact type) => Canonical(
        "type",
        [
            type.Kind.ToString(),
            type.Nullability.ToString(),
            type.AssemblyName ?? string.Empty,
            type.MetadataName ?? string.Empty,
            string.Join("|", type.TypeArguments.Select(CanonicalType)),
            type.ElementType is null ? string.Empty : CanonicalType(type.ElementType),
            type.ArrayRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            type.TypeParameterOwner?.ToString() ?? string.Empty,
            type.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            type.Name ?? string.Empty,
        ]);

    private static string CanonicalComponents(
        ImmutableArray<DocumentationScribeSemanticApplicableComponent> components) =>
        string.Join("|", components.Select(item => $"{item.Kind}:{item.Identity}:{item.Name}"));

    private static string CanonicalDocumentation(DocumentationScribeSemanticDocumentationState value) =>
        $"{value.Value}:{value.Completeness}:{value.UnavailableCause}";

    private static string CanonicalSource(DocumentationScribeSemanticSourceEvidence source) => Canonical(
        "source",
        [
            SourceViewSortKey(source),
            source.ContentSha256,
            source.IncludedContentSha256,
            source.OriginalUtf8ByteCount.ToString(CultureInfo.InvariantCulture),
            source.IncludedUtf8ByteCount.ToString(CultureInfo.InvariantCulture),
            source.IsTruncated ? "1" : "0",
            source.HasUtf8Bom ? "1" : "0",
            source.Range.Start.ToString(CultureInfo.InvariantCulture),
            source.Range.End.ToString(CultureInfo.InvariantCulture),
            source.IncludedRange?.Start.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            source.IncludedRange?.End.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ]);

    private static string CanonicalItem(DocumentationScribeSemanticEvidenceItem item) => Canonical(
        "item",
        [
            item.ItemIdentity,
            item.Kind.ToString(),
            item.UsageKind?.ToString() ?? string.Empty,
            item.RelationKind?.ToString() ?? string.Empty,
            item.RelationDirection?.ToString() ?? string.Empty,
            item.RelatedSymbolRef?.CompilationContextRef ?? string.Empty,
            item.RelatedSymbolRef?.DocumentationCommentId ?? string.Empty,
            CanonicalSource(item.Source),
        ]);

    private static string SourceViewSortKey(DocumentationScribeSemanticSourceEvidence source) => source switch
    {
        DocumentationScribeSemanticRepositoryEvidence repository => "0|" + repository.RepositoryPath,
        DocumentationScribeSemanticSourceGeneratorEvidence generated =>
            "1|" + generated.ProducerId + "|" + generated.OutputId,
        DocumentationScribeSemanticToolGeneratedEvidence generated =>
            "2|" + generated.ProducerId + "|" + generated.OutputId,
        _ => throw new InvalidOperationException("semantic.source.view-not-admitted"),
    };

    private static string SourceSortKey(LoadedSourceTree source) => source.Kind switch
    {
        LoadedSourceKind.Repository => "0|" + source.RepositoryPath,
        LoadedSourceKind.SourceGenerator =>
            "1|" + source.GeneratedSource?.ProducerId + "|" + source.GeneratedSource?.OutputId,
        LoadedSourceKind.ToolGenerated =>
            "2|" + source.GeneratedSource?.ProducerId + "|" + source.GeneratedSource?.OutputId,
        _ => throw new InvalidOperationException("semantic.source.kind-not-admitted"),
    };

    private static string SourceSortKey(DocumentationSourceIdentity source) => source switch
    {
        RepositoryDocumentationSourceIdentity repository => "0|" + repository.Path,
        GeneratedDocumentationSourceIdentity generated =>
            (generated.Kind == DocumentationSourceKind.SourceGenerator ? "1|" : "2|")
            + generated.ProducerId + "|" + generated.OutputId,
        _ => throw new InvalidOperationException("semantic.source.identity-not-admitted"),
    };

    private static IEnumerable<string> ParentDirectories(string root, string file)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Directory.GetParent(file)?.FullName;
        var directories = new Stack<string>();
        while (current is not null
               && !string.Equals(current, rootFull, PathComparison()))
        {
            directories.Push(current);
            current = Directory.GetParent(current)?.FullName;
        }

        while (directories.Count > 0)
        {
            yield return directories.Pop();
        }
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsNameOfExpression(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax identifier
        && string.Equals(identifier.Identifier.ValueText, "nameof", StringComparison.Ordinal);

    private static bool IsDeclarationName(SimpleNameSyntax name) => name.Parent switch
    {
        MethodDeclarationSyntax method => method.Identifier.Span == name.Identifier.Span,
        LocalFunctionStatementSyntax local => local.Identifier.Span == name.Identifier.Span,
        _ => false,
    };

    private static string FullMetadataName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            names.Push(current.MetadataName);
        }

        var prefix = type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "."
            : string.Empty;
        return prefix + string.Join("+", names);
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

        var implementation = definition is IMethodSymbol method
            ? method.PartialImplementationPart
            : null;
        if (implementation is null)
        {
            yield break;
        }

        foreach (var reference in implementation.DeclaringSyntaxReferences)
        {
            yield return reference;
        }
    }

    private static DocumentationScribeSemanticAccessibility Accessibility(Accessibility accessibility) =>
        accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Private => DocumentationScribeSemanticAccessibility.Private,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal =>
                DocumentationScribeSemanticAccessibility.PrivateProtected,
            Microsoft.CodeAnalysis.Accessibility.Internal => DocumentationScribeSemanticAccessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.Protected => DocumentationScribeSemanticAccessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal =>
                DocumentationScribeSemanticAccessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.Public => DocumentationScribeSemanticAccessibility.Public,
            _ => DocumentationScribeSemanticAccessibility.NotApplicable,
        };

    private static DocumentationScribeSemanticAccessibility EffectiveAccessibility(IMethodSymbol method)
    {
        var result = Accessibility(method.DeclaredAccessibility);
        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            var containing = Accessibility(type.DeclaredAccessibility);
            if (AccessibilityRank(containing) < AccessibilityRank(result))
            {
                result = containing;
            }
        }

        return result;
    }

    private static int AccessibilityRank(DocumentationScribeSemanticAccessibility accessibility) =>
        accessibility switch
        {
            DocumentationScribeSemanticAccessibility.Private => 0,
            DocumentationScribeSemanticAccessibility.PrivateProtected => 1,
            DocumentationScribeSemanticAccessibility.Internal => 2,
            DocumentationScribeSemanticAccessibility.Protected => 2,
            DocumentationScribeSemanticAccessibility.ProtectedInternal => 3,
            DocumentationScribeSemanticAccessibility.Public => 4,
            _ => -1,
        };

    private static DocumentationScribeSemanticRefKind RefKind(RefKind kind) => kind switch
    {
        Microsoft.CodeAnalysis.RefKind.None => DocumentationScribeSemanticRefKind.None,
        Microsoft.CodeAnalysis.RefKind.Ref => DocumentationScribeSemanticRefKind.Ref,
        Microsoft.CodeAnalysis.RefKind.Out => DocumentationScribeSemanticRefKind.Out,
        Microsoft.CodeAnalysis.RefKind.In => DocumentationScribeSemanticRefKind.In,
        Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter =>
            DocumentationScribeSemanticRefKind.RefReadOnly,
        _ => throw new UnsupportedSignatureException(),
    };

    private static DocumentationScribeSemanticNullability Nullability(NullableAnnotation annotation) =>
        annotation switch
        {
            NullableAnnotation.None => DocumentationScribeSemanticNullability.Oblivious,
            NullableAnnotation.NotAnnotated => DocumentationScribeSemanticNullability.NotAnnotated,
            NullableAnnotation.Annotated => DocumentationScribeSemanticNullability.Annotated,
            _ => throw new UnsupportedSignatureException(),
        };

    private static void AddIncomplete(
        ICollection<DocumentationScribeSemanticIncomplete> incomplete,
        DocumentationScribeSemanticIncompleteReason reason,
        int count) => incomplete.Add(new(reason, count));

    private sealed record Binding(
        ClassificationSet? ClassificationSet,
        TargetClassification? Target,
        LoadedProject? Project,
        IMethodSymbol? Method,
        ImmutableArray<DocumentationScribeSemanticApplicableComponent> ApplicableComponents,
        DocumentationScribeSemanticFailureReason? FailureReason)
    {
        internal static Binding Failed(
            DocumentationScribeSemanticFailureReason reason,
            ClassificationSet? set = null,
            TargetClassification? target = null,
            IMethodSymbol? method = null) => new(set, target, null, method, [], reason);
    }

    private sealed record Snapshot(
        DocumentationScribeSemanticTargetCore Core,
        ImmutableArray<DocumentationScribeSemanticEvidenceItem> Items,
        ImmutableArray<DocumentationScribeSemanticIncomplete> Incomplete,
        ImmutableArray<SourceValidation> Validations);

    private sealed record Materialized(
        DocumentationScribeEvidenceContextFact Evidence,
        DocumentationScribeSemanticSourceEvidence Source,
        SourceValidation? Validation);

    private sealed record CandidateItem(
        int Priority,
        string SortKey,
        string CanonicalMaterial,
        DocumentationScribeSemanticEvidenceItem Item)
    {
        internal static CandidateItem Create(
            int priority,
            Materialized materialized,
            DocumentationScribeSemanticEvidenceKind kind,
            DocumentationScribeSemanticUsageKind? usageKind,
            RelationKind? relationKind,
            DocumentationScribeSemanticRelationDirection? relationDirection,
            SymbolRef? relatedSymbolRef,
            Func<string, string> identity)
        {
            var canonical = DocumentationScribeSemanticToolPort.Canonical(
                "semantic.item.v1",
                [
                    materialized.Evidence.EvidenceId,
                    kind.ToString(),
                    usageKind?.ToString() ?? string.Empty,
                    relationKind?.ToString() ?? string.Empty,
                    relationDirection?.ToString() ?? string.Empty,
                    relatedSymbolRef?.CompilationContextRef ?? string.Empty,
                    relatedSymbolRef?.DocumentationCommentId ?? string.Empty,
                ]);
            var itemIdentity = identity(canonical);
            var item = new DocumentationScribeSemanticEvidenceItem(
                itemIdentity,
                kind,
                materialized.Source,
                usageKind,
                relationKind,
                relationDirection,
                relatedSymbolRef);
            return new CandidateItem(
                priority,
                SourceViewSortKey(materialized.Source)
                    + "|" + materialized.Source.Range.Start.ToString("D10", CultureInfo.InvariantCulture)
                    + "|" + materialized.Source.Range.End.ToString("D10", CultureInfo.InvariantCulture)
                    + "|" + kind + "|" + itemIdentity,
                canonical,
                item);
        }
    }

    private sealed record Occurrence(
        string Key,
        LoadedProject Project,
        SyntaxTree Tree,
        LoadedSourceTree Source,
        TextSpan Span,
        DocumentationScribeSemanticUsageKind Kind,
        bool IsTest);

    private readonly record struct Anchor(
        LoadedProject Project,
        SyntaxTree Tree,
        LoadedSourceTree Source,
        TextSpan Span);

    private sealed record DirectoryValidation(
        string Path,
        DocumentationScribeContextPhysicalIdentity Identity);

    private abstract record SourceValidation
    {
        internal abstract string Key { get; }
    }

    private sealed record RepositorySourceValidation(
        string RepositoryPath,
        string PhysicalPath,
        string ExpectedPhysicalIdentity,
        string ExpectedText,
        string Root,
        DocumentationScribeContextPhysicalIdentity RootIdentity,
        ImmutableArray<DirectoryValidation> Directories,
        DocumentationScribeContextPhysicalIdentity FileIdentity,
        byte[] Bytes,
        string SourceSha256,
        bool HasUtf8Bom) : SourceValidation
    {
        internal override string Key => RepositoryPath + "|" + SourceSha256;
    }


    private sealed record GeneratedSourceValidation(
        string CompilationContextRef,
        SyntaxTree Tree,
        LoadedSourceTree Source,
        string ExpectedText,
        string SourceSha256) : SourceValidation
    {
        internal override string Key =>
            CompilationContextRef + "|" + Source.Kind + "|" + Source.GeneratedSource?.ProducerId
            + "|" + Source.GeneratedSource?.OutputId + "|" + SourceSha256;
    }

    private sealed class SemanticTimeoutException : Exception;

    private sealed class SemanticBudgetException : Exception;

    private sealed class SemanticIdentityCollisionException : Exception;

    private sealed class UnsupportedSignatureException : Exception;

    private sealed class SemanticTraversalCompleteException : Exception;
}
