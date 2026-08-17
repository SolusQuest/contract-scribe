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
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.Roslyn;

internal enum DocumentationScribeSemanticStage
{
    Binding,
    Target,
    DocumentationObservation,
    Documentation,
    Relations,
    Usages,
    UsageTraversal,
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
    private const int MaximumSymbolFacts = 4096;
    private const int MaximumCursorUtf8Bytes = 4096;
    private const int ResultEnvelopeReserveUtf8Bytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DocumentationScribeLoadedContext loadedContext;
    private readonly DocumentationScribeRequest scribeRequest;
    private readonly DocumentationScribeSemanticToolLimits limits;
    private readonly int remainingEvidenceReferences;
    private readonly int remainingEvidenceUtf8Bytes;
    private readonly Action<DocumentationScribeSemanticStage>? stageObserver;
    private readonly Action<DocumentationScribeContextObservationEvent>? observationObserver;
    private readonly Func<string, string> identity;
    private readonly Func<long, TimeSpan> elapsed;
    private readonly Binding binding;

    public DocumentationScribeSemanticToolPort(
        DocumentationScribeLoadedContext loadedContext,
        DocumentationScribeRequest scribeRequest,
        DocumentationScribeSemanticToolLimits? limits = null)
        : this(loadedContext, scribeRequest, limits, null, null, null, null)
    {
    }

    internal DocumentationScribeSemanticToolPort(
        DocumentationScribeLoadedContext loadedContext,
        DocumentationScribeRequest scribeRequest,
        DocumentationScribeSemanticToolLimits? limits,
        Action<DocumentationScribeSemanticStage>? stageObserver,
        Func<string, string>? identity,
        Action<DocumentationScribeContextObservationEvent>? observationObserver = null,
        Func<long, TimeSpan>? elapsed = null)
    {
        this.loadedContext = loadedContext ?? throw new ArgumentNullException(nameof(loadedContext));
        this.scribeRequest = scribeRequest ?? throw new ArgumentNullException(nameof(scribeRequest));
        this.limits = Intersect(
            limits ?? DocumentationScribeSemanticToolLimits.Production,
            scribeRequest,
            out remainingEvidenceReferences,
            out remainingEvidenceUtf8Bytes);
        this.stageObserver = stageObserver;
        this.observationObserver = observationObserver;
        this.identity = identity ?? Sha256;
        this.elapsed = elapsed ?? Stopwatch.GetElapsedTime;

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

            if (!loadedContext.VerifyFreshness(
                    cancellationToken,
                    () => Check(DocumentationScribeSemanticStage.Binding, started, cancellationToken)))
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

            var declarationFact = loadedContext.Facts.Evidence.Single(item =>
                string.Equals(item.KindId, "source.target-declaration", StringComparison.Ordinal));
            if (remainingEvidenceReferences < 1
                || declarationFact.Commitment.OriginalUtf8ByteCount > limits.MaximumSourceFileUtf8Bytes
                || declarationFact.Commitment.IncludedUtf8ByteCount > limits.MaximumIncludedSourceUtf8Bytes
                || declarationFact.Commitment.IncludedUtf8ByteCount > remainingEvidenceUtf8Bytes)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.BudgetExhausted,
                    DocumentationScribeSemanticFailureReason.BudgetExhausted));
            }

            var snapshot = BuildSnapshot(declarationFact, started, cancellationToken);
            Check(DocumentationScribeSemanticStage.Normalize, started, cancellationToken);

            var coreOnlyResult = new DocumentationScribeSemanticToolResult(
                snapshot.Incomplete.IsEmpty
                    ? DocumentationScribeToolOutcome.Complete
                    : DocumentationScribeToolOutcome.Incomplete,
                new DocumentationScribeSemanticEvidencePage(
                    snapshot.Core,
                    [],
                    snapshot.Incomplete,
                    null),
                null);
            var coreBytes = MeasureResultBytes(coreOnlyResult);
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
                snapshot.Core.Declaration.Fact.Commitment.IncludedUtf8ByteCount,
                out var incomplete);
            var snapshotIdentity = SnapshotIdentity(snapshot.Core, normalizedItems, incomplete);
            var normalizedRequestIdentity = Identity(
                "semantic.normalized-request.v1",
                scribeRequest.ArtifactSha256,
                limits.MaximumOptionalItems.ToString(CultureInfo.InvariantCulture),
                limits.MaximumResultUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                limits.MaximumPageSize.ToString(CultureInfo.InvariantCulture),
                limits.MaximumSourceFileUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                limits.MaximumIncludedSourceUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                limits.MaximumCompilations.ToString(CultureInfo.InvariantCulture),
                limits.MaximumSourceTrees.ToString(CultureInfo.InvariantCulture),
                limits.MaximumSyntaxNodes.ToString(CultureInfo.InvariantCulture),
                limits.MaximumElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                remainingEvidenceReferences.ToString(CultureInfo.InvariantCulture),
                remainingEvidenceUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                CanonicalRequestEvidence(scribeRequest.EvidenceReferences),
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
                    cancellationToken,
                    () => Check(DocumentationScribeSemanticStage.Page, started, cancellationToken)))
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
            ValidateFinalSources(snapshot.Validations, started, cancellationToken);
            if (!loadedContext.VerifyFreshness(
                    cancellationToken,
                    () => Check(DocumentationScribeSemanticStage.FinalFreshness, started, cancellationToken)))
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
                cancellationToken,
                () => Check(DocumentationScribeSemanticStage.Cursor, started, cancellationToken));

            ValidateFinalSources(snapshot.Validations, started, cancellationToken);
            if (!loadedContext.VerifyFreshness(
                    cancellationToken,
                    () => Check(DocumentationScribeSemanticStage.FinalFreshness, started, cancellationToken)))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.StaleContext));
            }

            var outcome = incomplete.IsEmpty
                ? DocumentationScribeToolOutcome.Complete
                : DocumentationScribeToolOutcome.Incomplete;
            var result = new DocumentationScribeSemanticToolResult(
                outcome,
                new DocumentationScribeSemanticEvidencePage(
                    snapshot.Core,
                    pageItems,
                    incomplete,
                    nextCursor),
                null);
            if (MeasureResultBytes(result) > limits.MaximumResultUtf8Bytes)
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.BudgetExhausted,
                    DocumentationScribeSemanticFailureReason.BudgetExhausted));
            }

            Check(DocumentationScribeSemanticStage.Publish, started, cancellationToken);
            ValidateFinalSources(snapshot.Validations, started, cancellationToken);
            if (!loadedContext.VerifyFreshness(
                    cancellationToken,
                    () => Check(DocumentationScribeSemanticStage.Publish, started, cancellationToken)))
            {
                return ValueTask.FromResult(Terminal(
                    DocumentationScribeToolOutcome.Failure,
                    DocumentationScribeSemanticFailureReason.StaleContext));
            }
            Check(started, cancellationToken);

            return ValueTask.FromResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    private Snapshot BuildSnapshot(
        DocumentationScribeEvidenceContextFact declarationFact,
        long started,
        CancellationToken cancellationToken)
    {
        var method = binding.Method!;
        var target = binding.Target!;
        Check(DocumentationScribeSemanticStage.Target, started, cancellationToken);
        ValidateRepresentableMethod(method, () => Check(started, cancellationToken));
        var summary = CreateMethodSummary(method, target, started, cancellationToken);
        var selectedObservation = ObserveSelectedTarget(started, cancellationToken);
        Check(started, cancellationToken);
        var observation = selectedObservation.ObservationSet!.Observations.Single(item =>
            item.Subject.ParentSymbolRef == loadedContext.Facts.SymbolRef
            && item.Subject.ComponentKind is null);
        var documentationState = new DocumentationScribeSemanticDocumentationState(
            observation.Value,
            observation.Completeness,
            observation.UnavailableCause);
        var declaration = ToSourceView(
            declarationFact,
            loadedContext.Facts.CompilationContextRef);
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
        var materializationCache = new MaterializationCache();

        Check(DocumentationScribeSemanticStage.Documentation, started, cancellationToken);
        AddDocumentationItems(
            observation,
            items,
            incomplete,
            validations,
            materializationCache,
            started,
            cancellationToken);
        Check(DocumentationScribeSemanticStage.Relations, started, cancellationToken);
        AddRelationItems(
            items,
            incomplete,
            validations,
            materializationCache,
            started,
            cancellationToken);
        Check(DocumentationScribeSemanticStage.Usages, started, cancellationToken);
        try
        {
            AddUsageItems(
                items,
                incomplete,
                validations,
                materializationCache,
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

    private ObservedRepositorySession ObserveSelectedTarget(
        long started,
        CancellationToken cancellationToken)
    {
        var original = GetClassifiedSession(loadedContext);
        var set = binding.ClassificationSet!;
        var symbolRef = loadedContext.Facts.SymbolRef;
        var selectedSet = CreateClassificationSet(
            set.TargetProfile,
            [binding.Target!],
            set.Components
                .Where(item => item.ParentSymbolRef == symbolRef
                    && DocumentationObserver.IsObservableComponent(item))
                .ToImmutableArray(),
            set.Relations
                .Where(item => item.SourceSymbolRef == symbolRef || item.TargetSymbolRef == symbolRef)
                .ToImmutableArray(),
            []);
        var selectedSession = ClassifiedRepositorySession.Bind(
            original.RepositorySession,
            CreateClassificationOutcome(
                ClassificationRunStatus.Success,
                selectedSet,
                null,
                []));
        var observed = new DocumentationObserver(
            null,
            _ => Check(DocumentationScribeSemanticStage.DocumentationObservation, started, cancellationToken)).Observe(
            selectedSession,
            cancellationToken);
        Check(started, cancellationToken);
        if (observed.Status == DocumentationObservationRunStatus.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
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
        MaterializationCache materializationCache,
        long started,
        CancellationToken cancellationToken)
    {
        var remaining = Math.Max(0, limits.MaximumOptionalItems - items.Count);
        var declarations = observation.Declarations
            .Where(item => item.DocumentationSpan is not null)
            .OrderBy(item => SourceSortKey(item.Source), StringComparer.Ordinal)
            .ThenBy(item => item.DocumentationSpan!.Value.Start)
            .ToArray();
        if (declarations.Length > remaining)
        {
            AddIncomplete(incomplete, DocumentationScribeSemanticIncompleteReason.ItemLimit);
        }

        foreach (var declaration in declarations.Take(remaining))
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
                materializationCache,
                started,
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
        MaterializationCache materializationCache,
        long started,
        CancellationToken cancellationToken)
    {
        var remaining = Math.Max(0, limits.MaximumOptionalItems - items.Count);
        var selected = new SortedDictionary<string, RelationObservation>(StringComparer.Ordinal);
        var relationLimitReached = false;
        foreach (var relation in binding.ClassificationSet!.Relations)
        {
            Check(started, cancellationToken);
            if (relation.SourceSymbolRef != loadedContext.Facts.SymbolRef
                && relation.TargetSymbolRef != loadedContext.Facts.SymbolRef)
            {
                continue;
            }
            var key = relation.RelationKind + "|"
                + relation.SourceSymbolRef.CompilationContextRef + "|"
                + relation.SourceSymbolRef.DocumentationCommentId + "|"
                + relation.TargetSymbolRef.CompilationContextRef + "|"
                + relation.TargetSymbolRef.DocumentationCommentId;
            selected[key] = relation;
            if (selected.Count > remaining)
            {
                selected.Remove(selected.Keys.Last());
                relationLimitReached = true;
            }
        }
        if (relationLimitReached)
        {
            AddIncomplete(incomplete, DocumentationScribeSemanticIncompleteReason.ItemLimit);
        }

        foreach (var relation in selected.Values)
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
                materializationCache,
                started,
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
        MaterializationCache materializationCache,
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
        var occurrenceLimitReached = false;
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
                Check(started, cancellationToken);
                var model = project.Compilation.GetSemanticModel(pair.Key, ignoreAccessibility: true);
                Check(started, cancellationToken);

                foreach (var node in root.DescendantNodesAndSelf())
                {
                    CountNode(ref syntaxNodeCount, incomplete);
                    Check(DocumentationScribeSemanticStage.UsageTraversal, started, cancellationToken);
                    if (node is InvocationExpressionSyntax invocation)
                    {
                        var operation = model.GetOperation(invocation, cancellationToken);
                        Check(started, cancellationToken);
                        if (operation is INameOfOperation
                            && NameOfArgumentResolvesToTarget(
                                project,
                                model,
                                invocation,
                                started,
                                cancellationToken))
                        {
                            AddOccurrence(
                                occurrences,
                                project,
                                pair.Key,
                                pair.Value,
                                invocation.Span,
                                DocumentationScribeSemanticUsageKind.NameOf,
                                isTest: false,
                                ref occurrenceLimitReached);
                        }
                        else if (operation is IInvocationOperation invoked
                                 && ResolvesToTarget(project, invoked.TargetMethod))
                        {
                            AddOccurrence(
                                occurrences,
                                project,
                                pair.Key,
                                pair.Value,
                                invocation.Span,
                                DocumentationScribeSemanticUsageKind.Invocation,
                                IsTestOccurrence(model, invocation.SpanStart, cancellationToken),
                                ref occurrenceLimitReached);
                        }

                        continue;
                    }

                    if (node is not SimpleNameSyntax name
                        || IsDeclarationName(name)
                        || IsConsumedInvocationName(project, model, name, cancellationToken)
                        || !ResolvesToTarget(
                            project,
                            model.GetSymbolInfo(name, cancellationToken).Symbol))
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
                        IsTestOccurrence(model, name.SpanStart, cancellationToken),
                        ref occurrenceLimitReached);
                }
            }
        }

        if (occurrenceLimitReached)
        {
            AddIncomplete(incomplete, DocumentationScribeSemanticIncompleteReason.ItemLimit);
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
                materializationCache,
                started,
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
        bool isTest,
        ref bool limitReached)
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
        var occurrence = new Occurrence(
            key,
            project,
            tree,
            source,
            span,
            kind,
            isTest);
        if (occurrences.ContainsKey(key))
        {
            return;
        }

        var capacity = limits.MaximumOptionalItems;
        if (capacity == 0)
        {
            limitReached = true;
            return;
        }
        if (occurrences.Count < capacity)
        {
            occurrences.Add(key, occurrence);
            return;
        }

        limitReached = true;
        var worst = occurrences.Values
            .OrderBy(item => item.IsTest ? 0 : 1)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Last();
        if (CompareOccurrences(occurrence, worst) < 0)
        {
            occurrences.Remove(worst.Key);
            occurrences.Add(key, occurrence);
        }
    }

    private static int CompareOccurrences(Occurrence left, Occurrence right)
    {
        var priority = (left.IsTest ? 0 : 1).CompareTo(right.IsTest ? 0 : 1);
        return priority != 0
            ? priority
            : StringComparer.Ordinal.Compare(left.Key, right.Key);
    }

    private bool NameOfArgumentResolvesToTarget(
        LoadedProject project,
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        long started,
        CancellationToken cancellationToken)
    {
        foreach (var node in invocation.ArgumentList.DescendantNodesAndSelf())
        {
            Check(started, cancellationToken);
            var symbolInfo = model.GetSymbolInfo(node, cancellationToken);
            var symbols = symbolInfo.Symbol is not null
                ? ImmutableArray.Create(symbolInfo.Symbol)
                : !symbolInfo.CandidateSymbols.IsEmpty
                    ? symbolInfo.CandidateSymbols
                    : node is ExpressionSyntax expression
                        ? model.GetMemberGroup(expression, cancellationToken)
                        : [];
            var methods = symbols
                .Select(symbol => symbol is IAliasSymbol alias ? alias.Target : symbol)
                .OfType<IMethodSymbol>()
                .Select(NormalizeMethod)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToArray();
            if (methods.Length == 1 && ResolvesToTarget(project, methods[0]))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsConsumedInvocationName(
        LoadedProject project,
        SemanticModel model,
        SimpleNameSyntax name,
        CancellationToken cancellationToken)
    {
        var invocation = name.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null || !invocation.Expression.Span.Contains(name.Span))
        {
            return false;
        }

        return model.GetOperation(invocation, cancellationToken) switch
        {
            INameOfOperation => true,
            IInvocationOperation operation => ResolvesToTarget(project, operation.TargetMethod),
            _ => false,
        };
    }

    private Materialized? Materialize(
        DocumentationSourceIdentity source,
        Utf16Span span,
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string kindId,
        List<DocumentationScribeSemanticIncomplete> incomplete,
        List<SourceValidation> validations,
        MaterializationCache materializationCache,
        long started,
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
                materializationCache,
                started,
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
        MaterializationCache materializationCache,
        long started,
        CancellationToken cancellationToken)
    {
        if (!materializationCache.SourceTexts.TryGetValue(tree, out var text))
        {
            text = tree.GetText(cancellationToken).ToString();
            materializationCache.SourceTexts.Add(tree, text);
        }
        Check(started, cancellationToken);
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

            if (!materializationCache.RepositoryReads.TryGetValue(source.RepositoryPath, out var repository))
            {
                try
                {
                    repository = ReadRepositorySource(
                        source.RepositoryPath,
                        source.PhysicalSourceIdentity,
                        text,
                        started,
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

                materializationCache.RepositoryReads.Add(source.RepositoryPath, repository);
            }
            if (repository is null)
            {
                AddIncomplete(
                    incomplete,
                    DocumentationScribeSemanticIncompleteReason.UnsupportedEncoding,
                    1);
                return null;
            }
            if (!string.Equals(repository.ExpectedPhysicalIdentity, source.PhysicalSourceIdentity, StringComparison.Ordinal)
                || !string.Equals(repository.ExpectedText, text, StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.cached-source");
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
                || !string.Equals(fact.SourceText, text, StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.generated-source");
            }

            if (!materializationCache.GeneratedReads.TryGetValue(tree, out var generatedValidation))
            {
                var bytes = StrictUtf8.GetBytes(text);
                if (bytes.Length > limits.MaximumSourceFileUtf8Bytes)
                {
                    AddIncomplete(
                        incomplete,
                        DocumentationScribeSemanticIncompleteReason.SourceByteLimit,
                        1);
                    return null;
                }
                if (!string.Equals(Sha256(bytes), fact.SourceSha256, StringComparison.Ordinal))
                {
                    throw new DocumentationScribeContextReadException(
                        DocumentationScribeContextReadFailure.Stale,
                        "semantic.stale.generated-source");
                }
                generatedValidation = new GeneratedSourceValidation(
                    project.CompilationContextRef,
                    tree,
                    source,
                    text,
                    bytes,
                    fact.SourceSha256);
                materializationCache.GeneratedReads.Add(tree, generatedValidation);
            }
            else if (!string.Equals(generatedValidation.CompilationContextRef, project.CompilationContextRef, StringComparison.Ordinal)
                     || !ReferenceEquals(generatedValidation.Source, source)
                     || !string.Equals(generatedValidation.ExpectedText, text, StringComparison.Ordinal)
                     || !string.Equals(generatedValidation.SourceSha256, fact.SourceSha256, StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Stale,
                    "semantic.stale.generated-source");
            }
            validation = generatedValidation;
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
            var includedBytes = StrictUtf8.GetBytes(content);
            commitment = DocumentationScribeContextValidation.CreateEvidenceSourceCommitment(
                locator,
                fact.SourceSha256,
                Sha256(includedBytes),
                generatedValidation.Bytes.Length,
                includedBytes.Length,
                includedBytes.Length < generatedValidation.Bytes.Length,
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
        return new Materialized(
            evidence,
            ToSourceView(evidence, project.CompilationContextRef),
            validation);
    }

    private RepositorySourceValidation? ReadRepositorySource(
        string repositoryPath,
        string expectedPhysicalIdentity,
        string expectedText,
        long started,
        CancellationToken cancellationToken)
    {
        Check(started, cancellationToken);
        var root = loadedContext.RepositorySession.PhysicalRepositoryRoot;
        if (!string.Equals(
                LogicalPhysicalIdentity(repositoryPath),
                expectedPhysicalIdentity,
                StringComparison.Ordinal))
        {
            throw new DocumentationScribeContextReadException(
                DocumentationScribeContextReadFailure.Unsafe,
                "semantic.unsafe.physical-binding");
        }

        DocumentationScribeContextObservedRead observed;
        try
        {
            observed = DocumentationScribeContextStableFileReader.CaptureRegularFile(
                root,
                loadedContext.RepositoryRootIdentity,
                repositoryPath,
                limits.MaximumSourceFileUtf8Bytes,
                cancellationToken,
                () => Check(started, cancellationToken),
                observationObserver);
            Check(started, cancellationToken);
        }
        catch (DocumentationScribeContextReadException exception)
            when (exception.Failure == DocumentationScribeContextReadFailure.Budget)
        {
            throw new SemanticBudgetException();
        }

        var read = observed.Read;
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
        Check(started, cancellationToken);

        return new RepositorySourceValidation(
            repositoryPath,
            expectedPhysicalIdentity,
            expectedText,
            observed.Observation,
            read.Identity,
            read.Bytes,
            Sha256(read.Bytes),
            hasBom);
    }

    private void ValidateFinalSources(
        ImmutableArray<SourceValidation> validations,
        long started,
        CancellationToken cancellationToken)
    {
        foreach (var validation in validations)
        {
            Check(started, cancellationToken);
            if (validation is GeneratedSourceValidation generated)
            {
                ValidateGeneratedSource(generated, started, cancellationToken);
                continue;
            }

            var repository = (RepositorySourceValidation)validation;
            if (!string.Equals(
                    LogicalPhysicalIdentity(repository.RepositoryPath),
                    repository.ExpectedPhysicalIdentity,
                    StringComparison.Ordinal))
            {
                throw new DocumentationScribeContextReadException(
                    DocumentationScribeContextReadFailure.Unsafe,
                    "semantic.unsafe.physical-binding");
            }

            var observed = DocumentationScribeContextStableFileReader.ReadCapturedFile(
                loadedContext.RepositorySession.PhysicalRepositoryRoot,
                loadedContext.RepositoryRootIdentity,
                repository.Observation,
                limits.MaximumSourceFileUtf8Bytes,
                acceptedBytes: true,
                cancellationToken,
                () => Check(started, cancellationToken),
                observationObserver);
            Check(started, cancellationToken);
            var read = observed.Read;
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
            Check(started, cancellationToken);
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
        long started,
        CancellationToken cancellationToken)
    {
        Check(started, cancellationToken);
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
        Check(started, cancellationToken);
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
        int coreEvidenceBytes,
        out ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete)
    {
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeSemanticEvidenceItem>();
        var incompleteBuilder = initialIncomplete.ToBuilder();
        var bytes = checked(
            coreBytes + MaximumCursorUtf8Bytes + ResultEnvelopeReserveUtf8Bytes);
        var evidenceBytes = coreEvidenceBytes;
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
            var itemEvidenceBytes = item.Source.Fact.Commitment.IncludedUtf8ByteCount;
            if (bytes + itemBytes > limits.MaximumResultUtf8Bytes
                || evidenceBytes + itemEvidenceBytes > remainingEvidenceUtf8Bytes)
            {
                AddIncomplete(
                    incompleteBuilder,
                    DocumentationScribeSemanticIncompleteReason.ResultByteLimit,
                    items.Length - builder.Count);
                break;
            }

            builder.Add(item);
            bytes += itemBytes;
            evidenceBytes += itemEvidenceBytes;
        }

        incomplete = incompleteBuilder
            .GroupBy(item => item.Reason)
            .OrderBy(group => group.Key)
            .Select(group => new DocumentationScribeSemanticIncomplete(group.Key))
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

    private bool ResolvesToTarget(LoadedProject project, ISymbol? symbol)
    {
        if (!CanResolveSelectedProjectFrom(project))
        {
            return false;
        }

        symbol = symbol is IAliasSymbol alias ? alias.Target : symbol;
        if (symbol is not IMethodSymbol method)
        {
            return false;
        }

        method = NormalizeMethod(method);
        if (!method.ContainingAssembly.Identity.Equals(binding.Method!.ContainingAssembly.Identity)
            || !string.Equals(
                method.GetDocumentationCommentId(),
                loadedContext.Facts.SymbolRef.DocumentationCommentId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var exactSymbols = DocumentationCommentId.GetSymbolsForDeclarationId(
                loadedContext.Facts.SymbolRef.DocumentationCommentId,
                project.Compilation)
            .OfType<IMethodSymbol>()
            .Select(NormalizeMethod)
            .Where(candidate => candidate.ContainingAssembly.Identity.Equals(
                binding.Method.ContainingAssembly.Identity))
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        return exactSymbols.Length == 1
            && SymbolEqualityComparer.Default.Equals(exactSymbols[0], method);
    }

    private bool CanResolveSelectedProjectFrom(LoadedProject project)
    {
        var selectedIdentity = binding.Project!.ProjectIdentity;
        var projects = loadedContext.RepositorySession.Projects
            .GroupBy(item => item.ProjectIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(project.ProjectIdentity);
        while (pending.TryPop(out var identity))
        {
            if (!visited.Add(identity))
            {
                continue;
            }
            if (string.Equals(identity, selectedIdentity, StringComparison.Ordinal))
            {
                return true;
            }
            if (!projects.TryGetValue(identity, out var current))
            {
                continue;
            }
            foreach (var reference in current.ProjectReferences)
            {
                pending.Push(reference);
            }
        }

        return false;
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

        for (IMethodSymbol? candidate = method; candidate is not null; candidate = candidate.ContainingSymbol as IMethodSymbol)
        {
            if (candidate.GetAttributes().Any(attribute =>
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
            }))
            {
                return true;
            }
        }

        return false;
    }

    private DocumentationScribeSemanticMethodSummary CreateMethodSummary(
        IMethodSymbol method,
        TargetClassification target,
        long started,
        CancellationToken cancellationToken)
    {
        var symbolFacts = 0;
        DocumentationScribeSemanticTypeFact ProjectType(ITypeSymbol type, int depth)
        {
            Check(started, cancellationToken);
            if (++symbolFacts > MaximumSymbolFacts)
            {
                throw new SemanticBudgetException();
            }
            return CreateType(type, depth, ProjectType);
        }

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
                ProjectType(item.Type, 0),
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
                item.ConstraintTypes.Select(type => ProjectType(type, 0)).ToImmutableArray()))
            .ToImmutableArray();
        return new DocumentationScribeSemanticMethodSummary(
            loadedContext.Facts.SymbolRef,
            target.Traits
                .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal)
                .ToImmutableArray(),
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
            ProjectType(method.ReturnType, 0),
            parameters,
            typeParameters);
    }

    private static DocumentationScribeSemanticTypeFact CreateType(
        ITypeSymbol type,
        int depth,
        Func<ITypeSymbol, int, DocumentationScribeSemanticTypeFact> projectType)
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
                 null,
                 [],
                projectType(array.ElementType, depth + 1),
                array.Rank,
                null,
                null,
                null),
            IPointerTypeSymbol pointer => new(
                DocumentationScribeSemanticTypeKind.Pointer,
                nullability,
                 null,
                 null,
                 null,
                 [],
                projectType(pointer.PointedAtType, depth + 1),
                null,
                null,
                null,
                null),
            ITypeParameterSymbol parameter => new(
                DocumentationScribeSemanticTypeKind.TypeParameter,
                nullability,
                 null,
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
                 named.ContainingAssembly?.Identity.GetDisplayName(),
                 FullMetadataName(named.OriginalDefinition),
                 named.ContainingType is null
                     ? null
                     : projectType(named.ContainingType, depth + 1),
                 named.TypeArguments.Select(item => projectType(item, depth + 1)).ToImmutableArray(),
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

    // UnsafeAccessor is managed runtime field access, not native interop. X3 uses it only to
    // consume the exact X1-owned classified session without rerunning or widening M1 selection.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "classifiedSession")]
    private static extern ref readonly ClassifiedRepositorySession GetClassifiedSession(
        DocumentationScribeLoadedContext context);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ClassificationSet CreateClassificationSet(
        TargetProfile targetProfile,
        ImmutableArray<TargetClassification> targets,
        ImmutableArray<ComponentClassification> components,
        ImmutableArray<RelationObservation> relations,
        ImmutableArray<UnresolvedClassification> unresolved);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ClassificationOutcome CreateClassificationOutcome(
        ClassificationRunStatus status,
        ClassificationSet? classificationSet,
        ClassificationRunFailure? primaryFailure,
        ImmutableArray<ClassificationDiagnostic> diagnostics);

    private static bool ValidateRepresentableMethod(IMethodSymbol method, Action? checkpoint = null)
    {
        var symbolFacts = 0;
        void ValidateType(ITypeSymbol type, int depth)
        {
            checkpoint?.Invoke();
            if (++symbolFacts > MaximumSymbolFacts)
            {
                throw new UnsupportedSignatureException();
            }
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
                    if (named.ContainingType is not null)
                    {
                        ValidateType(named.ContainingType, depth + 1);
                    }
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
        DocumentationScribeEvidenceContextFact evidence,
        string compilationContextRef)
    {
        if (evidence.Range is null
            || evidence.Commitment.Locator is not (RepositoryEvidenceLocator or GeneratedOutputEvidenceLocator))
        {
            throw new InvalidOperationException("semantic.source.locator-not-admitted");
        }

        var correlationIdentity = Identity(
            "semantic.source-correlation.v1",
            loadedContext.Facts.RepositoryContextRef.Value,
            compilationContextRef,
            scribeRequest.ArtifactSha256,
            evidence.EvidenceId);
        return new DocumentationScribeSemanticSourceEvidence(
            evidence,
            compilationContextRef,
            correlationIdentity);
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

    private static DocumentationScribeSemanticToolLimits Intersect(
        DocumentationScribeSemanticToolLimits semantic,
        DocumentationScribeRequest request,
        out int remainingReferences,
        out int remainingUtf8Bytes)
    {
        var existingBytes = request.EvidenceReferences.Aggregate(
            0L,
            (total, reference) => checked(total + reference.IncludedUtf8ByteCount));
        remainingReferences = Math.Max(
            0,
            request.Limits.MaximumEvidenceReferences - request.EvidenceReferences.Length);
        remainingUtf8Bytes = (int)Math.Max(
            0L,
            (long)request.Limits.MaximumEvidenceUtf8Bytes - existingBytes);
        return new DocumentationScribeSemanticToolLimits(
            semantic.MaximumPageSize,
            Math.Min(semantic.MaximumOptionalItems, Math.Max(0, remainingReferences - 1)),
            Math.Min(semantic.MaximumResultUtf8Bytes, DocumentationScribeContract.MaximumArtifactUtf8Bytes),
            semantic.MaximumSourceFileUtf8Bytes,
            semantic.MaximumIncludedSourceUtf8Bytes,
            semantic.MaximumCompilations,
            semantic.MaximumSourceTrees,
            semantic.MaximumSyntaxNodes,
            Math.Min(semantic.MaximumElapsedMilliseconds, request.Limits.MaximumElapsedMilliseconds));
    }

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
        if (elapsed(started).TotalMilliseconds > limits.MaximumElapsedMilliseconds)
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
                CanonicalSourceForResult(core.Declaration),
            ]));

    private static int EstimateItemBytes(DocumentationScribeSemanticEvidenceItem item) =>
        StrictUtf8.GetByteCount(CanonicalItemForResult(item));

    private static int MeasureResultBytes(DocumentationScribeSemanticToolResult result)
    {
        var page = result.Page;
        var material = Canonical(
            "result",
            [
                result.Outcome.Id,
                result.FailureReason?.ToString() ?? string.Empty,
                page is null ? string.Empty : CanonicalCoreForResult(page.Core),
                page is null ? string.Empty : string.Join("|", page.Items.Select(CanonicalItemForResult)),
                page is null ? string.Empty : string.Join("|", page.Incomplete.Select(item => item.Reason.ToString())),
                page?.NextCursor?.Value ?? string.Empty,
            ]);
        return StrictUtf8.GetByteCount(material);
    }

    private static string CanonicalCoreForResult(DocumentationScribeSemanticTargetCore core) => Canonical(
        "core-result",
        [
            core.ContentIdentity,
            core.CorrelationIdentity,
            CanonicalMethod(core.Method),
            CanonicalComponents(core.ApplicableComponents),
            CanonicalDocumentation(core.Documentation),
            CanonicalSourceForResult(core.Declaration),
        ]);

    private static string CanonicalRequestEvidence(
        ImmutableArray<DocumentationScribeEvidenceReference> references) =>
        string.Join(
            "|",
            references.OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
                .Select(item => Canonical(
                    "request-evidence",
                    [
                        item.EvidenceReferenceId,
                        item.IncludedUtf8ByteCount.ToString(CultureInfo.InvariantCulture),
                    ])));

    private string SnapshotIdentity(
        DocumentationScribeSemanticTargetCore core,
        ImmutableArray<DocumentationScribeSemanticEvidenceItem> items,
        ImmutableArray<DocumentationScribeSemanticIncomplete> incomplete) => Identity(
            "semantic.snapshot.v1",
            core.ContentIdentity,
            string.Join("|", items.Select(CanonicalItem)),
            string.Join("|", incomplete.Select(item => item.Reason.ToString())));

    private static string CanonicalMethod(DocumentationScribeSemanticMethodSummary method) => Canonical(
        "method",
        [
            method.SymbolRef.CompilationContextRef,
            method.SymbolRef.DocumentationCommentId,
            method.PrimaryKind.ToString(),
            string.Join(",", method.Traits
                .OrderBy(ClassificationVocabulary.GetId, StringComparer.Ordinal)
                .Select(ClassificationVocabulary.GetId)),
            method.Origin.ToString(),
            method.MetadataName,
            method.ContainingNamespace,
            method.ContainingTypeSymbolRef.DocumentationCommentId,
            method.DeclaredAccessibility.ToString(),
            method.EffectiveAccessibility.ToString(),
            method.ReturnRefKind.ToString(),
            CanonicalType(method.ReturnType),
            string.Join("", method.Parameters.Select(item => Canonical(
                "parameter",
                [
                    item.Ordinal.ToString(CultureInfo.InvariantCulture),
                    item.Name,
                    CanonicalType(item.Type),
                    item.RefKind.ToString(),
                    item.IsParams ? "1" : "0",
                    item.IsOptional ? "1" : "0",
                ]))),
            string.Join("", method.TypeParameters.Select(item => Canonical(
                "type-parameter",
                [
                    item.Ordinal.ToString(CultureInfo.InvariantCulture),
                    item.Name,
                    item.HasReferenceTypeConstraint ? "1" : "0",
                    item.HasValueTypeConstraint ? "1" : "0",
                    item.HasUnmanagedConstraint ? "1" : "0",
                    item.HasNotNullConstraint ? "1" : "0",
                    item.HasConstructorConstraint ? "1" : "0",
                    item.ReferenceTypeConstraintNullability.ToString(),
                    string.Join("", item.ConstraintTypes.Select(CanonicalType)),
                ]))),
        ]);

    private static string CanonicalType(DocumentationScribeSemanticTypeFact type) => Canonical(
        "type",
        [
            type.Kind.ToString(),
            type.Nullability.ToString(),
            type.AssemblyIdentity ?? string.Empty,
            type.MetadataName ?? string.Empty,
            type.ContainingType is null ? string.Empty : CanonicalType(type.ContainingType),
            string.Join("|", type.TypeArguments.Select(CanonicalType)),
            type.ElementType is null ? string.Empty : CanonicalType(type.ElementType),
            type.ArrayRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            type.TypeParameterOwner?.ToString() ?? string.Empty,
            type.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            type.Name ?? string.Empty,
        ]);

    private static string CanonicalComponents(
        ImmutableArray<DocumentationScribeSemanticApplicableComponent> components) =>
        string.Join("", components.Select(item => Canonical(
            "component",
            [item.Kind.ToString(), item.Identity, item.Name ?? string.Empty])));

    private static string CanonicalDocumentation(DocumentationScribeSemanticDocumentationState value) =>
        $"{value.Value}:{value.Completeness}:{value.UnavailableCause}";

    private static string CanonicalSource(DocumentationScribeSemanticSourceEvidence source)
    {
        var fact = source.Fact;
        var commitment = fact.Commitment;
        return Canonical(
            "source",
            [
                fact.EvidenceId,
                fact.Authority.ToString(),
                fact.Role.ToString(),
                fact.SubjectId,
                fact.KindId,
                source.CompilationContextRef,
                CanonicalLocator(commitment.Locator),
                commitment.ContentSha256,
                commitment.IncludedContentSha256,
                commitment.OriginalUtf8ByteCount.ToString(CultureInfo.InvariantCulture),
                commitment.IncludedUtf8ByteCount.ToString(CultureInfo.InvariantCulture),
                commitment.IsTruncated ? "1" : "0",
                commitment.HasUtf8Bom ? "1" : "0",
                commitment.IncludedHasUtf8Bom ? "1" : "0",
                fact.Range?.Start.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                fact.Range?.End.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                fact.IncludedRange?.Start.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                fact.IncludedRange?.End.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                fact.Content,
            ]);
    }

    private static string CanonicalSourceForResult(DocumentationScribeSemanticSourceEvidence source) =>
        Canonical("source-result", [CanonicalSource(source), source.CorrelationIdentity]);

    private static string CanonicalLocator(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository => Canonical(
            "repository",
            [
                repository.Path,
                repository.Span?.Start.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                repository.Span?.End.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ]),
        GeneratedOutputEvidenceLocator generated => Canonical(
            "generated",
            [
                generated.ProducerKind.ToString(),
                generated.ProducerId,
                generated.OutputId,
                generated.SourceSha256,
                generated.Span?.Start.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                generated.Span?.End.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ]),
        MetadataEvidenceLocator metadata => Canonical(
            "metadata",
            [metadata.AssemblyIdentity, metadata.DocumentationCommentId]),
        SyntheticEvidenceLocator synthetic => Canonical("synthetic", [synthetic.FixtureId]),
        _ => throw new InvalidOperationException("semantic.source.locator-not-admitted"),
    };

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

    private static string CanonicalItemForResult(DocumentationScribeSemanticEvidenceItem item) =>
        Canonical("item-result", [CanonicalItem(item), CanonicalSourceForResult(item.Source)]);

    private static string SourceViewSortKey(DocumentationScribeSemanticSourceEvidence source) => source switch
    {
        { Fact.Commitment.Locator: RepositoryEvidenceLocator repository } =>
            "0|" + repository.Path + "|" + source.CompilationContextRef,
        { Fact.Commitment.Locator: GeneratedOutputEvidenceLocator generated } =>
            (generated.ProducerKind == GeneratedOutputKind.SourceGenerator ? "1|" : "2|")
            + generated.ProducerId + "|" + generated.OutputId + "|" + source.CompilationContextRef,
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

    private static string LogicalPhysicalIdentity(string repositoryPath) =>
        OperatingSystem.IsWindows() ? repositoryPath.ToUpperInvariant() : repositoryPath;

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
            result = IntersectAccessibility(result, Accessibility(type.DeclaredAccessibility));
        }

        return result;
    }

    private static DocumentationScribeSemanticAccessibility IntersectAccessibility(
        DocumentationScribeSemanticAccessibility left,
        DocumentationScribeSemanticAccessibility right)
    {
        if (left == DocumentationScribeSemanticAccessibility.NotApplicable
            || right == DocumentationScribeSemanticAccessibility.NotApplicable)
        {
            return DocumentationScribeSemanticAccessibility.NotApplicable;
        }
        if (left == DocumentationScribeSemanticAccessibility.Private
            || right == DocumentationScribeSemanticAccessibility.Private)
        {
            return DocumentationScribeSemanticAccessibility.Private;
        }
        if (left == DocumentationScribeSemanticAccessibility.Public)
        {
            return right;
        }
        if (right == DocumentationScribeSemanticAccessibility.Public)
        {
            return left;
        }
        if (left == right)
        {
            return left;
        }
        if (left == DocumentationScribeSemanticAccessibility.PrivateProtected
            || right == DocumentationScribeSemanticAccessibility.PrivateProtected
            || (left == DocumentationScribeSemanticAccessibility.Internal
                && right == DocumentationScribeSemanticAccessibility.Protected)
            || (left == DocumentationScribeSemanticAccessibility.Protected
                && right == DocumentationScribeSemanticAccessibility.Internal))
        {
            return DocumentationScribeSemanticAccessibility.PrivateProtected;
        }
        if (left == DocumentationScribeSemanticAccessibility.ProtectedInternal)
        {
            return right;
        }
        if (right == DocumentationScribeSemanticAccessibility.ProtectedInternal)
        {
            return left;
        }

        throw new InvalidOperationException("semantic.accessibility.intersection-not-admitted");
    }

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
        int count = 1)
    {
        _ = count;
        incomplete.Add(new(reason));
    }

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
                    materialized.Source.CompilationContextRef,
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
                    + "|" + materialized.Source.Fact.Range!.Value.Start.ToString("D10", CultureInfo.InvariantCulture)
                    + "|" + materialized.Source.Fact.Range!.Value.End.ToString("D10", CultureInfo.InvariantCulture)
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

    private sealed class MaterializationCache
    {
        internal Dictionary<SyntaxTree, string> SourceTexts { get; } = [];

        internal Dictionary<string, RepositorySourceValidation?> RepositoryReads { get; }
            = new(StringComparer.Ordinal);

        internal Dictionary<SyntaxTree, GeneratedSourceValidation> GeneratedReads { get; } = [];
    }

    private abstract record SourceValidation
    {
        internal abstract string Key { get; }
    }

    private sealed record RepositorySourceValidation(
        string RepositoryPath,
        string ExpectedPhysicalIdentity,
        string ExpectedText,
        DocumentationScribeContextPathObservation Observation,
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
        byte[] Bytes,
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
