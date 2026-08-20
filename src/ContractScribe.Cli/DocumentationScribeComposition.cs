using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal enum DocumentationScribeCompositionStatus
{
    PreflightRejected,
    ProposalSkipped,
    ProposalRejected,
    PatchAccepted,
    PatchRejected,
    PatchStale,
    ProviderFailure,
    RuntimeFailure,
    Cancelled,
    Timeout,
    BudgetExhausted,
}

internal sealed class DocumentationScribeCompositionOutcome
{
    private DocumentationScribeCompositionOutcome(
        DocumentationScribeCompositionStatus status,
        string code,
        DocumentationScribeRunResult? runResult,
        DocumentationPatchRequest? patchRequest,
        DocumentationPatchExecutionOutcome? patchOutcome)
    {
        Status = status;
        Code = code;
        RunResult = runResult;
        PatchRequest = patchRequest;
        PatchOutcome = patchOutcome;
    }

    internal DocumentationScribeCompositionStatus Status { get; }

    internal string Code { get; }

    internal DocumentationScribeRunResult? RunResult { get; }

    internal DocumentationPatchRequest? PatchRequest { get; }

    internal DocumentationPatchExecutionOutcome? PatchOutcome { get; }

    internal DocumentationPatchAcceptedCandidate? AcceptedCandidate =>
        Status == DocumentationScribeCompositionStatus.PatchAccepted
            ? PatchOutcome?.AcceptedCandidate
            : null;

    internal static DocumentationScribeCompositionOutcome Create(
        DocumentationScribeCompositionStatus status,
        string code,
        DocumentationScribeRunResult? runResult = null,
        DocumentationPatchRequest? patchRequest = null,
        DocumentationPatchExecutionOutcome? patchOutcome = null) =>
        new(status, code, runResult, patchRequest, patchOutcome);

    public override string ToString() => nameof(DocumentationScribeCompositionOutcome);
}

internal sealed class DocumentationScribeAuditAuthority
{
    private readonly ClassifiedRepositorySession session;
    private readonly ObservedRepositorySession observations;
    private readonly ImmutableArray<SelectedRow> rows;

    private DocumentationScribeAuditAuthority(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        ImmutableArray<SelectedRow> rows)
    {
        this.session = session;
        this.observations = observations;
        this.rows = rows;
    }

    internal static DocumentationScribeAuditAuthority Create(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        PolicyDocumentV1 acceptedPolicy,
        IEnumerable<AuditRecordInput> acceptedInputs,
        AuditDocument acceptedDocument)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(acceptedPolicy);
        ArgumentNullException.ThrowIfNull(acceptedInputs);
        ArgumentNullException.ThrowIfNull(acceptedDocument);
        if (!IsCurrent(session, observations)
            || session.Classification.ClassificationSet is not { } classifications)
        {
            throw new ArgumentException("scribe.audit.session-mismatch", nameof(session));
        }

        var inputs = acceptedInputs.ToImmutableArray();
        if (inputs.IsDefaultOrEmpty || inputs.Any(input => input is null))
        {
            throw new ArgumentException("scribe.audit.inputs-invalid", nameof(acceptedInputs));
        }

        var recomputed = AuditAggregator.Aggregate(
            classifications.TargetProfile,
            classifications,
            acceptedPolicy,
            inputs);
        var acceptedBytes = AuditJson.Write(acceptedDocument);
        var recomputedBytes = AuditJson.Write(recomputed);
        if (!acceptedBytes.AsSpan().SequenceEqual(recomputedBytes))
        {
            throw new ArgumentException("scribe.audit.document-mismatch", nameof(acceptedDocument));
        }

        using var parsed = JsonDocument.Parse(acceptedBytes);
        var resultRows = parsed.RootElement.GetProperty("results").EnumerateArray().ToArray();
        var selected = ImmutableArray.CreateBuilder<SelectedRow>();
        foreach (var target in classifications.Targets)
        {
            var input = inputs.OfType<TargetAuditInput>()
                .SingleOrDefault(candidate => ReferenceEquals(candidate.Classification, target));
            if (input is null)
            {
                throw new ArgumentException("scribe.audit.target-input-missing", nameof(acceptedInputs));
            }

            var matching = resultRows.Where(row => RowMatchesTarget(row, target)).ToArray();
            if (matching.Length != 1)
            {
                throw new ArgumentException("scribe.audit.target-row-invalid", nameof(acceptedDocument));
            }

            selected.Add(new SelectedRow(target, input, matching[0].Clone(), ParseOutcome(matching[0])));
        }

        return new DocumentationScribeAuditAuthority(
            session,
            observations,
            selected.ToImmutable());
    }

    internal DocumentationScribeSelectedAudit Select(TargetClassification target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsCurrent(session, observations))
        {
            throw new InvalidOperationException("scribe.audit.session-stale");
        }

        var row = rows.SingleOrDefault(candidate => ReferenceEquals(candidate.Target, target));
        if (row is null)
        {
            throw new ArgumentException("scribe.audit.target-not-member", nameof(target));
        }

        return new DocumentationScribeSelectedAudit(
            session,
            observations,
            row.Target,
            row.Input,
            row.CanonicalRow,
            row.Outcome);
    }

    private static bool RowMatchesTarget(JsonElement row, TargetClassification target)
    {
        if (!row.TryGetProperty("classification", out var classification)
            || !classification.TryGetProperty("recordType", out var recordType)
            || recordType.GetString() != "TargetClassification"
            || !classification.TryGetProperty("symbolRef", out var symbol))
        {
            return false;
        }

        return symbol.GetProperty("compilationContextRef").GetString()
                == target.SymbolRef.CompilationContextRef
            && symbol.GetProperty("documentationCommentId").GetString()
                == target.SymbolRef.DocumentationCommentId;
    }

    private static AuditOutcome ParseOutcome(JsonElement row) =>
        row.GetProperty("auditOutcome").GetString() switch
        {
            "audit.outcome.compliant" => AuditOutcome.Compliant,
            "audit.outcome.violation" => AuditOutcome.Violation,
            "audit.outcome.skipped" => AuditOutcome.Skipped,
            _ => throw new ArgumentException("scribe.audit.outcome-invalid"),
        };

    private static bool IsCurrent(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations) =>
        session.IsBoundToClassificationSession
        && !session.RepositorySession.IsDisposed
        && observations.IsBoundToObservationSession(session)
        && session.Classification.Status == ClassificationRunStatus.Success
        && session.Classification.ClassificationSet is not null
        && observations.Status == DocumentationObservationRunStatus.Success
        && observations.ObservationSet is not null;

    private sealed record SelectedRow(
        TargetClassification Target,
        TargetAuditInput Input,
        JsonElement CanonicalRow,
        AuditOutcome Outcome);

    public override string ToString() => nameof(DocumentationScribeAuditAuthority);
}

internal sealed class DocumentationScribeSelectedAudit
{
    internal DocumentationScribeSelectedAudit(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        TargetClassification target,
        TargetAuditInput input,
        JsonElement canonicalRow,
        AuditOutcome outcome)
    {
        Session = session;
        Observations = observations;
        Target = target;
        Input = input;
        CanonicalRow = canonicalRow;
        Outcome = outcome;
    }

    internal ClassifiedRepositorySession Session { get; }

    internal ObservedRepositorySession Observations { get; }

    internal TargetClassification Target { get; }

    internal TargetAuditInput Input { get; }

    internal JsonElement CanonicalRow { get; }

    internal AuditOutcome Outcome { get; }

    internal bool IsCurrent =>
        Session.IsBoundToClassificationSession
        && !Session.RepositorySession.IsDisposed
        && Observations.IsBoundToObservationSession(Session)
        && Session.Classification.Status == ClassificationRunStatus.Success
        && Session.Classification.ClassificationSet is { } classifications
        && classifications.Targets.Any(candidate => ReferenceEquals(candidate, Target))
        && Observations.Status == DocumentationObservationRunStatus.Success
        && Observations.ObservationSet is not null
        && ReferenceEquals(Input.Classification, Target);

    public override string ToString() => nameof(DocumentationScribeSelectedAudit);
}

internal static class DocumentationScribeComposition
{
    private static readonly JsonSerializerOptions ToolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly ReadOnlyMemory<byte> SemanticInputSchema = Encoding.UTF8.GetBytes(
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"cursor\":{\"type\":\"string\"}}}");

    internal static async Task<DocumentationScribeCompositionOutcome> ExecuteAsync(
        DocumentationScribeSelectedAudit selection,
        ReadOnlyMemory<byte> requestUtf8Json,
        DocumentationScribeAttemptId attemptId,
        string? configuredAgentEntrypoint,
        DocumentationScribeRuntimeOptions runtimeOptions,
        IDocumentationScribeModelExchange exchange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentNullException.ThrowIfNull(exchange);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = DocumentationScribeValidation.ParseRequest(requestUtf8Json);
            if (!parsed.IsValid || parsed.Request is not { } request)
            {
                return Reject("scribe.preflight.request-invalid");
            }

            var preflight = Preflight(selection, request, configuredAgentEntrypoint, cancellationToken);
            if (preflight.FailureCode is { } failureCode)
            {
                return Reject(failureCode);
            }

            var loaded = preflight.Context!;
            var prompt = BuildPrompt(selection, request, loaded);
            if (prompt is null)
            {
                return Reject("scribe.preflight.prompt-evidence-mismatch");
            }

            var registry = BuildRegistry(
                request,
                attemptId,
                loaded,
                preflight.SourceReference!);
            var runtime = new DocumentationScribeRuntime(exchange, registry, runtimeOptions);
            var run = await runtime.RunAsync(
                request,
                attemptId,
                prompt,
                cancellationToken).ConfigureAwait(false);

            if (run.Terminal is DocumentationScribeCancelledTerminal)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.Cancelled,
                    "scribe.cancelled",
                    run);
            }

            if (run.Terminal is DocumentationScribeFailureTerminal terminalFailure)
            {
                var status = terminalFailure.Code switch
                {
                    DocumentationScribeFailureCode.Provider =>
                        DocumentationScribeCompositionStatus.ProviderFailure,
                    DocumentationScribeFailureCode.Timeout =>
                        DocumentationScribeCompositionStatus.Timeout,
                    DocumentationScribeFailureCode.Budget =>
                        DocumentationScribeCompositionStatus.BudgetExhausted,
                    _ => DocumentationScribeCompositionStatus.RuntimeFailure,
                };
                return DocumentationScribeCompositionOutcome.Create(
                    status,
                    DocumentationScribeVocabulary.GetId(terminalFailure.Code),
                    run);
            }

            if (run.Terminal is DocumentationScribeSkipTerminal)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.ProposalSkipped,
                    "scribe.proposal.skipped",
                    run);
            }

            if (run.Terminal is not DocumentationScribeProposalTerminal proposal
                || !TerminalMatches(request, attemptId, run, proposal))
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.ProposalRejected,
                    "scribe.proposal.correlation-invalid",
                    run);
            }

            var postflight = Preflight(selection, request, configuredAgentEntrypoint, cancellationToken);
            var postflightFailure = postflight.FailureCode;
            if (postflightFailure is not null
                || !loaded.VerifyFreshness(cancellationToken))
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.PatchStale,
                    postflightFailure ?? "scribe.patch.stale-context",
                    run);
            }

            var patchBytes = BuildPatchRequest(
                request,
                postflight.Declaration!,
                postflight.EditKind,
                proposal.PatchContent,
                proposal.ContentUnits);
            var patchParse = DocumentationPatchValidator.ParseRequest(patchBytes);
            if (!patchParse.IsValid || patchParse.Request is not { } patchRequest)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.ProposalRejected,
                    "scribe.patch.request-invalid",
                    run);
            }

            var patchOutcome = new DocumentationPatchEngine().Execute(
                selection.Session,
                patchRequest,
                cancellationToken);
            if (patchOutcome.Status == DocumentationPatchExecutionStatus.HostFailure)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.PatchRejected,
                    patchOutcome.FailureCode ?? "scribe.patch.host-failure",
                    run,
                    patchRequest,
                    patchOutcome);
            }

            var patchStatus = patchOutcome.Result?.Outcome switch
            {
                DocumentationPatchOutcome.Accepted
                    when patchOutcome.AcceptedCandidate is not null =>
                    DocumentationScribeCompositionStatus.PatchAccepted,
                DocumentationPatchOutcome.Stale => DocumentationScribeCompositionStatus.PatchStale,
                _ => DocumentationScribeCompositionStatus.PatchRejected,
            };
            return DocumentationScribeCompositionOutcome.Create(
                patchStatus,
                patchStatus == DocumentationScribeCompositionStatus.PatchAccepted
                    ? "scribe.patch.accepted"
                    : patchStatus == DocumentationScribeCompositionStatus.PatchStale
                        ? "scribe.patch.stale"
                        : "scribe.patch.rejected",
                run,
                patchRequest,
                patchOutcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.Cancelled,
                "scribe.cancelled.caller");
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.RuntimeFailure,
                "scribe.failure.internal");
        }
    }

    private static PreflightResult Preflight(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeRequest request,
        string? configuredAgentEntrypoint,
        CancellationToken cancellationToken)
    {
        if (!selection.IsCurrent
            || request.Context.RepositoryContextRef
                != selection.Session.RepositorySession.RepositoryContextRef
            || !string.Equals(
                request.Context.InputIdentity,
                selection.Session.RepositorySession.InputIdentity,
                StringComparison.Ordinal)
            || request.Context.TargetProfile
                != selection.Session.Classification.ClassificationSet!.TargetProfile
            || request.Context.AuditOutcome != selection.Outcome
            || request.Target.SymbolRef != selection.Target.SymbolRef
            || selection.Target.PrimaryKind != PrimarySymbolKind.Method
            || selection.Target.SupportStatus != SupportStatus.Supported
            || request.Target.SourceLocator is not RepositoryEvidenceLocator locator
            || locator.Span is null)
        {
            return PreflightResult.Rejected("scribe.preflight.authority-mismatch");
        }

        var sourceReference = FindSourceReference(request, locator);
        if (sourceReference is null)
        {
            return PreflightResult.Rejected("scribe.preflight.source-evidence-mismatch");
        }

        var bootstrapSelection = DocumentationScribeContextValidation.CreateBootstrapSelection(
            request.Context.RepositoryContextRef,
            request.Context.InputIdentity,
            request.Context.TargetProfile,
            request.Target.SymbolRef,
            request.Target.SourceLocator,
            request.Target.SourceSha256,
            configuredAgentEntrypoint);
        var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(
            selection.Session,
            bootstrapSelection,
            cancellationToken);
        if (bootstrap.Status is not (DocumentationScribeContextBootstrapStatus.Succeeded
                or DocumentationScribeContextBootstrapStatus.Incomplete)
            || bootstrap.Context is not { } context)
        {
            var code = bootstrap.Status switch
            {
                DocumentationScribeContextBootstrapStatus.Cancelled => "scribe.preflight.cancelled",
                DocumentationScribeContextBootstrapStatus.TimedOut => "scribe.preflight.timeout",
                DocumentationScribeContextBootstrapStatus.BudgetExhausted => "scribe.preflight.budget",
                _ => bootstrap.Failure?.Code ?? "scribe.preflight.context-unavailable",
            };
            return PreflightResult.Rejected(code);
        }

        if (!context.ValidateRequestBinding(request).IsValid
            || !context.VerifyFreshness(cancellationToken))
        {
            return PreflightResult.Rejected("scribe.preflight.context-mismatch");
        }

        var capture = selection.Session.RepositorySession
            .CaptureDocumentationPatchResolutionBaseline(cancellationToken);
        if (capture.Baseline is not { } baseline
            || !baseline.TryGetEntry(locator.Path, out var entry)
            || !string.Equals(entry.Sha256, request.Target.SourceSha256, StringComparison.Ordinal))
        {
            return PreflightResult.Rejected(
                capture.FailureCode ?? "scribe.preflight.source-stale");
        }

        var probeBytes = BuildPatchRequest(
            request,
            locator,
            entry.Bytes,
            DocumentationPatchEditKind.Insert,
            content: null,
            []);
        var probeParse = DocumentationPatchValidator.ParseRequest(probeBytes);
        if (!probeParse.IsValid || probeParse.Request is not { } probeRequest)
        {
            return PreflightResult.Rejected("scribe.preflight.patch-probe-invalid");
        }

        var resolution = new DocumentationPatchDeclarationResolver().Resolve(
            selection.Session,
            probeRequest,
            baseline,
            cancellationToken);
        var block = resolution.RootFailureCode is null && resolution.Blocks.Length == 1
            ? resolution.Blocks[0]
            : null;
        if (block is null || !block.Failures.IsEmpty || block.Declaration is not { } declaration)
        {
            return PreflightResult.Rejected(
                block?.Failures.FirstOrDefault()?.Code
                    ?? resolution.RootFailureCode
                    ?? "scribe.preflight.declaration-unavailable");
        }

        var editKind = declaration.BlockState switch
        {
            DocumentationBlockState.NoBlock => DocumentationPatchEditKind.Insert,
            DocumentationBlockState.WhitespaceOnly or DocumentationBlockState.WellFormed =>
                DocumentationPatchEditKind.Replace,
            _ => (DocumentationPatchEditKind?)null,
        };
        if (editKind is null
            || declaration.RepositoryPath != locator.Path
            || declaration.SourceSha256 != request.Target.SourceSha256
            || declaration.RequestedDeclarationSpan != locator.Span.Value
            || !ComponentsEqual(request.Target.ApplicableComponents, declaration.ApplicableComponents))
        {
            return PreflightResult.Rejected("scribe.preflight.edit-authorization-unavailable");
        }

        return new PreflightResult(
            context,
            declaration,
            editKind.Value,
            sourceReference,
            null);
    }

    private static DocumentationScribePromptInput? BuildPrompt(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeRequest request,
        DocumentationScribeLoadedContext context)
    {
        var contextContent = ImmutableArray.CreateBuilder<DocumentationScribeContextContent>();
        foreach (var reference in request.ContextReferences)
        {
            var fact = context.Facts.Instructions.SingleOrDefault(candidate =>
                candidate.Commitment.RepositoryPath == reference.Path
                && candidate.Commitment.ContentSha256 == reference.ContentSha256
                && candidate.Commitment.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
                && candidate.Commitment.IsTruncated == reference.IsTruncated);
            if (fact is null)
            {
                return null;
            }

            contextContent.Add(new DocumentationScribeContextContent(
                reference.ContextReferenceId,
                reference.Kind,
                reference.ContentSha256,
                reference.IncludedUtf8ByteCount,
                reference.IsTruncated,
                fact.Content));
        }

        var evidenceContent = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceContent>();
        foreach (var reference in request.EvidenceReferences)
        {
            var loadedFact = context.Facts.Evidence.SingleOrDefault(candidate =>
                candidate.Commitment.ContentSha256 == reference.ContentSha256
                && candidate.Commitment.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
                && candidate.Commitment.IsTruncated == reference.IsTruncated);
            var content = loadedFact?.Content ?? AuditEvidenceContent(selection.CanonicalRow, reference);
            if (content is null)
            {
                return null;
            }

            evidenceContent.Add(new DocumentationScribeEvidenceContent(
                reference.EvidenceReferenceId,
                reference.Authority,
                reference.ContentSha256,
                reference.IncludedUtf8ByteCount,
                reference.IsTruncated,
                content));
        }

        return new DocumentationScribePromptInput(
            contextContent.ToImmutable(),
            evidenceContent.ToImmutable());
    }

    private static string? AuditEvidenceContent(
        JsonElement row,
        DocumentationScribeEvidenceReference reference)
    {
        if (!row.TryGetProperty("evidenceBundle", out var bundle)
            || !bundle.TryGetProperty("items", out var items))
        {
            return null;
        }

        var matching = items.EnumerateArray().Where(item =>
            item.GetProperty("evidenceId").GetString() == reference.EvidenceReferenceId
            && item.GetProperty("sha256").GetString() == reference.ContentSha256
            && item.GetProperty("originalUtf8ByteCount").GetInt32()
                == reference.OriginalUtf8ByteCount
            && item.GetProperty("includedUtf8ByteCount").GetInt32()
                == reference.IncludedUtf8ByteCount
            && item.GetProperty("isTruncated").GetBoolean() == reference.IsTruncated).ToArray();
        return matching.Length == 1 ? matching[0].GetProperty("excerpt").GetString() : null;
    }

    private static DocumentationScribeEvidenceReference? FindSourceReference(
        DocumentationScribeRequest request,
        RepositoryEvidenceLocator locator)
    {
        var matching = request.EvidenceReferences.Where(reference =>
            reference.Subject is TargetEvidenceSubject subject
            && subject.ParentSymbolRef == request.Target.SymbolRef
            && reference.Locator is RepositoryEvidenceLocator evidenceLocator
            && evidenceLocator == locator
            && reference.Kind == EvidenceKind.SourceDeclaration
            && reference.Relation == EvidenceRelation.Declares
            && reference.Authority == DocumentationScribeEvidenceAuthority.SourceDeclaration)
            .ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private static DocumentationScribeToolRegistry BuildRegistry(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeLoadedContext context,
        DocumentationScribeEvidenceReference sourceReference)
    {
        var locator = (RepositoryEvidenceLocator)request.Target.SourceLocator;
        var scope = DocumentationScribeRepositoryToolScope.File(
            sourceReference.EvidenceReferenceId,
            locator.Path,
            DocumentationScribeRepositoryToolOperations.ReadExcerpt
                | DocumentationScribeRepositoryToolOperations.SearchText,
            DocumentationScribeContextRole.SourceDeclaration,
            subject: sourceReference.Subject,
            kind: sourceReference.Kind,
            relation: sourceReference.Relation,
            authority: sourceReference.Authority,
            claimCategoryIds: sourceReference.ClaimCategoryIds);
        var repository = DocumentationScribeRepositoryToolBundle.Create(
            request,
            attemptId,
            context,
            [scope]);
        var builder = new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId)
            .Add(
                DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor,
                repository.ReadExcerpt,
                new ReadCodec(),
                DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription,
                DocumentationScribeRepositoryToolSchemas.ReadExcerptInputUtf8Json,
                maximumCallsPerRun: request.Limits.MaximumToolCalls)
            .Add(
                DocumentationScribeRepositoryToolBundle.ListFilesDescriptor,
                repository.ListFiles,
                new ListCodec(),
                DocumentationScribeRepositoryToolSchemas.ListFilesDescription,
                DocumentationScribeRepositoryToolSchemas.ListFilesInputUtf8Json,
                maximumCallsPerRun: request.Limits.MaximumToolCalls)
            .Add(
                DocumentationScribeRepositoryToolBundle.SearchTextDescriptor,
                repository.SearchText,
                new SearchCodec(),
                DocumentationScribeRepositoryToolSchemas.SearchTextDescription,
                DocumentationScribeRepositoryToolSchemas.SearchTextInputUtf8Json,
                maximumCallsPerRun: request.Limits.MaximumToolCalls)
            .Add(
                new DocumentationScribeSemanticToolDescriptor(),
                new DocumentationScribeSemanticToolPort(context, request),
                new SemanticCodec(),
                "Read one bounded semantic evidence page for the exact selected method.",
                SemanticInputSchema,
                maximumCallsPerRun: request.Limits.MaximumToolCalls);
        return builder.Build();
    }

    private static bool TerminalMatches(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRunResult run,
        DocumentationScribeProposalTerminal proposal) =>
        run.ScribeRequestSha256 == request.ArtifactSha256
        && run.AttemptId == attemptId
        && proposal.Target.RepositoryContextRef == request.Context.RepositoryContextRef
        && proposal.Target.SymbolRef == request.Target.SymbolRef
        && proposal.Target.SourceLocator == request.Target.SourceLocator
        && proposal.Target.SourceSha256 == request.Target.SourceSha256;

    private static bool ComponentsEqual(
        ImmutableArray<DocumentationPatchApplicableComponent> requested,
        ImmutableArray<DocumentationPatchResolvedComponent> resolved) =>
        requested.Length == resolved.Length
        && requested.Zip(resolved).All(pair =>
            pair.First.Kind == pair.Second.Kind
            && pair.First.Identity == pair.Second.Identity
            && pair.First.Name == pair.Second.Name);

    private static ReadOnlyMemory<byte> BuildPatchRequest(
        DocumentationScribeRequest request,
        DocumentationPatchResolvedDeclaration declaration,
        DocumentationPatchEditKind editKind,
        DocumentationPatchContent content,
        ImmutableArray<DocumentationScribeContentUnit> units) =>
        BuildPatchRequest(
            request,
            (RepositoryEvidenceLocator)request.Target.SourceLocator,
            default,
            editKind,
            content,
            units,
            declaration.Encoding);

    private static ReadOnlyMemory<byte> BuildPatchRequest(
        DocumentationScribeRequest request,
        RepositoryEvidenceLocator locator,
        ImmutableArray<byte> sourceBytes,
        DocumentationPatchEditKind editKind,
        DocumentationPatchContent? content,
        ImmutableArray<DocumentationScribeContentUnit> units,
        DocumentationPatchRepositoryEncoding? knownEncoding = null)
    {
        var provenance = units.SelectMany(unit => unit.EvidenceReferenceIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var encoding = knownEncoding ?? DetectEncoding(sourceBytes);
        var root = new JsonObject
        {
            ["patchRequestVersion"] = 1,
            ["context"] = new JsonObject
            {
                ["repositoryContextRef"] = request.Context.RepositoryContextRef.Value,
                ["inputIdentity"] = request.Context.InputIdentity,
                ["targetProfile"] = ClassificationVocabulary.GetId(request.Context.TargetProfile),
            },
            ["provenanceCatalog"] = Strings(provenance),
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["blockId"] = BlockId(request.Target.SymbolRef),
                    ["symbolRef"] = new JsonObject
                    {
                        ["compilationContextRef"] = request.Target.SymbolRef.CompilationContextRef,
                        ["documentationCommentId"] = request.Target.SymbolRef.DocumentationCommentId,
                    },
                    ["locator"] = new JsonObject
                    {
                        ["kind"] = "repository",
                        ["path"] = locator.Path,
                        ["originalFileSha256"] = request.Target.SourceSha256,
                        ["encoding"] = EncodingId(encoding),
                        ["declarationSpan"] = new JsonObject
                        {
                            ["start"] = locator.Span!.Value.Start,
                            ["end"] = locator.Span.Value.End,
                        },
                    },
                    ["editKind"] = editKind == DocumentationPatchEditKind.Insert
                        ? "insert"
                        : "replace",
                    ["applicableComponents"] = Components(request.Target.ApplicableComponents),
                    ["content"] = content is null
                        ? new JsonObject { ["kind"] = "inheritDoc" }
                        : PatchContent(content),
                    ["provenanceRefs"] = Strings(provenance),
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, ToolJson);
    }

    private static JsonArray Components(
        ImmutableArray<DocumentationPatchApplicableComponent> components)
    {
        var result = new JsonArray();
        foreach (var component in components)
        {
            result.Add(new JsonObject
            {
                ["kind"] = component.Kind switch
                {
                    DocumentationPatchComponentKind.TypeParameter => "typeParameter",
                    DocumentationPatchComponentKind.Parameter => "parameter",
                    DocumentationPatchComponentKind.Return => "return",
                    DocumentationPatchComponentKind.Value => "value",
                    _ => throw new InvalidOperationException("Unknown component kind."),
                },
                ["identity"] = component.Identity,
                ["name"] = component.Name,
            });
        }

        return result;
    }

    private static JsonObject PatchContent(DocumentationPatchContent content) => content switch
    {
        DocumentationPatchInheritDocContent => new JsonObject { ["kind"] = "inheritDoc" },
        DocumentationPatchStructuredContent structured => new JsonObject
        {
            ["kind"] = "structured",
            ["summaryLines"] = Strings(structured.SummaryLines),
            ["typeParameters"] = Named(structured.TypeParameters),
            ["parameters"] = Named(structured.Parameters),
            ["return"] = ComponentContent(structured.Return),
            ["value"] = ComponentContent(structured.Value),
            ["exceptions"] = Exceptions(structured.Exceptions),
            ["remarksLines"] = structured.RemarksLines is { } remarks
                ? Strings(remarks)
                : null,
        },
        _ => throw new InvalidOperationException("Unknown patch content."),
    };

    private static JsonArray Named(ImmutableArray<DocumentationPatchNamedContent> items)
    {
        var result = new JsonArray();
        foreach (var item in items)
        {
            result.Add(new JsonObject
            {
                ["componentIdentity"] = item.ComponentIdentity,
                ["name"] = item.Name,
                ["lines"] = Strings(item.Lines),
            });
        }

        return result;
    }

    private static JsonNode? ComponentContent(DocumentationPatchComponentContent? item) =>
        item is null
            ? null
            : new JsonObject
            {
                ["componentIdentity"] = item.ComponentIdentity,
                ["lines"] = Strings(item.Lines),
            };

    private static JsonArray Exceptions(
        ImmutableArray<DocumentationPatchExceptionContent> items)
    {
        var result = new JsonArray();
        foreach (var item in items)
        {
            result.Add(new JsonObject
            {
                ["typeDocumentationId"] = item.TypeDocumentationId,
                ["lines"] = Strings(item.Lines),
            });
        }

        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static DocumentationPatchRepositoryEncoding DetectEncoding(
        ImmutableArray<byte> bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return DocumentationPatchRepositoryEncoding.Utf8Bom;
        }

        if (span.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom;
        }

        if (span.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            return DocumentationPatchRepositoryEncoding.Utf16BigEndianBom;
        }

        return DocumentationPatchRepositoryEncoding.Utf8;
    }

    private static string EncodingId(DocumentationPatchRepositoryEncoding encoding) =>
        encoding switch
        {
            DocumentationPatchRepositoryEncoding.Utf8 => "utf-8",
            DocumentationPatchRepositoryEncoding.Utf8Bom => "utf-8-bom",
            DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "utf-16le-bom",
            DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "utf-16be-bom",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    private static string BlockId(SymbolRef symbol)
    {
        var bytes = Encoding.UTF8.GetBytes(
            symbol.CompilationContextRef + "\0" + symbol.DocumentationCommentId);
        return "block." + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DocumentationScribeCompositionOutcome Reject(string code) =>
        DocumentationScribeCompositionOutcome.Create(
            DocumentationScribeCompositionStatus.PreflightRejected,
            code);

    private sealed record PreflightResult(
        DocumentationScribeLoadedContext? Context,
        DocumentationPatchResolvedDeclaration? Declaration,
        DocumentationPatchEditKind EditKind,
        DocumentationScribeEvidenceReference? SourceReference,
        string? FailureCode)
    {
        internal static PreflightResult Rejected(string code) =>
            new(null, null, default, null, code);
    }

    private abstract class RepositoryCodec<TRequest, TResult>
        : IDocumentationScribeToolCodec<TRequest, TResult>
        where TRequest : IDocumentationScribeToolRequest<TResult>
        where TResult : IDocumentationScribeToolResult
    {
        public abstract DocumentationScribeToolDecodeResult<TRequest> DecodeArguments(
            ReadOnlyMemory<byte> argumentsUtf8Json);

        public DocumentationScribeToolEncodeResult EncodeResult(TRequest request, TResult result)
        {
            var evidence = result switch
            {
                DocumentationScribeRepositoryReadExcerptResult read => read.DynamicEvidence,
                DocumentationScribeRepositorySearchTextResult search => search.DynamicEvidence,
                _ => [],
            };
            object projection = result switch
            {
                DocumentationScribeRepositoryReadExcerptResult read => new
                {
                    outcome = read.Outcome.Id,
                    read.FailureCode,
                    read.Excerpt,
                    read.Route,
                },
                DocumentationScribeRepositoryListFilesResult list => new
                {
                    outcome = list.Outcome.Id,
                    list.FailureCode,
                    list.Items,
                    list.Cursor,
                },
                DocumentationScribeRepositorySearchTextResult search => new
                {
                    outcome = search.Outcome.Id,
                    search.FailureCode,
                    search.Items,
                    search.Cursor,
                    search.Routes,
                },
                _ => throw new InvalidOperationException("Unknown repository result."),
            };
            return DocumentationScribeToolEncodeResult.Accepted(
                new DocumentationScribeToolResultPayload(
                    JsonSerializer.SerializeToUtf8Bytes(projection, ToolJson),
                    evidence));
        }
    }

    private sealed class ReadCodec
        : RepositoryCodec<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult>
    {
        public override DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryReadExcerptRequest>
            DecodeArguments(ReadOnlyMemory<byte> json)
        {
            var root = StrictObject(json, "scopeId", "repositoryPath", "startLine", "endLine");
            if (root is not { } value || !RequiredString(value, "scopeId", out var scope))
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryReadExcerptRequest>.Rejected();
            }

            if (!OptionalString(value, "repositoryPath", out var path)
                || !OptionalInt(value, "startLine", out var start)
                || !OptionalInt(value, "endLine", out var end))
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryReadExcerptRequest>.Rejected();
            }

            try
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryReadExcerptRequest>.Accepted(
                    new(scope!, path, start, end));
            }
            catch (ArgumentException)
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryReadExcerptRequest>.Rejected();
            }
        }
    }

    private sealed class ListCodec
        : RepositoryCodec<DocumentationScribeRepositoryListFilesRequest, DocumentationScribeRepositoryListFilesResult>
    {
        public override DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryListFilesRequest>
            DecodeArguments(ReadOnlyMemory<byte> json)
        {
            var root = StrictObject(json, "scopeId", "subdirectory", "pageSize", "cursor");
            if (root is not { } value
                || !RequiredString(value, "scopeId", out var scope)
                || !OptionalString(value, "subdirectory", out var subdirectory)
                || !OptionalInt(value, "pageSize", out var pageSize)
                || !OptionalString(value, "cursor", out var cursor))
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryListFilesRequest>.Rejected();
            }

            try
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryListFilesRequest>.Accepted(
                    new(scope!, subdirectory, pageSize ?? 32, cursor));
            }
            catch (ArgumentException)
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositoryListFilesRequest>.Rejected();
            }
        }
    }

    private sealed class SearchCodec
        : RepositoryCodec<DocumentationScribeRepositorySearchTextRequest, DocumentationScribeRepositorySearchTextResult>
    {
        public override DocumentationScribeToolDecodeResult<DocumentationScribeRepositorySearchTextRequest>
            DecodeArguments(ReadOnlyMemory<byte> json)
        {
            var root = StrictObject(json, "scopeId", "literal", "subdirectory", "pageSize", "cursor");
            if (root is not { } value
                || !RequiredString(value, "scopeId", out var scope)
                || !RequiredString(value, "literal", out var literal)
                || !OptionalString(value, "subdirectory", out var subdirectory)
                || !OptionalInt(value, "pageSize", out var pageSize)
                || !OptionalString(value, "cursor", out var cursor))
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositorySearchTextRequest>.Rejected();
            }

            try
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositorySearchTextRequest>.Accepted(
                    new(scope!, literal!, subdirectory, pageSize ?? 32, cursor));
            }
            catch (ArgumentException)
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeRepositorySearchTextRequest>.Rejected();
            }
        }
    }

    private sealed class SemanticCodec
        : IDocumentationScribeToolCodec<DocumentationScribeSemanticToolRequest, DocumentationScribeSemanticToolResult>
    {
        public DocumentationScribeToolDecodeResult<DocumentationScribeSemanticToolRequest> DecodeArguments(
            ReadOnlyMemory<byte> json)
        {
            var root = StrictObject(json, "pageSize", "cursor");
            if (root is not { } value
                || !OptionalInt(value, "pageSize", out var pageSize)
                || !OptionalString(value, "cursor", out var cursorValue))
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeSemanticToolRequest>.Rejected();
            }

            DocumentationScribeContextCursor? cursor = null;
            if (cursorValue is not null)
            {
                if (!DocumentationScribeContextCursor.TryParse(cursorValue, out var parsed))
                {
                    return DocumentationScribeToolDecodeResult<DocumentationScribeSemanticToolRequest>.Rejected();
                }

                cursor = parsed;
            }

            try
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeSemanticToolRequest>.Accepted(
                    DocumentationScribeSemanticToolRequest.Create(pageSize ?? 20, cursor));
            }
            catch (ArgumentException)
            {
                return DocumentationScribeToolDecodeResult<DocumentationScribeSemanticToolRequest>.Rejected();
            }
        }

        public DocumentationScribeToolEncodeResult EncodeResult(
            DocumentationScribeSemanticToolRequest request,
            DocumentationScribeSemanticToolResult result) =>
            DocumentationScribeToolEncodeResult.Accepted(
                new DocumentationScribeToolResultPayload(
                    JsonSerializer.SerializeToUtf8Bytes(result, ToolJson),
                    []));
    }

    private static JsonElement? StrictObject(
        ReadOnlyMemory<byte> json,
        params string[] allowed)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(root)
                || root.EnumerateObject().Any(property =>
                    !allowed.Contains(property.Name, StringComparer.Ordinal)))
            {
                return null;
            }

            return root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }

    private static bool RequiredString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool OptionalString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool OptionalInt(JsonElement root, string name, out int? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (!property.TryGetInt32(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
