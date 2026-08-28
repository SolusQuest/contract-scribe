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
    ProposalReady,
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
        DocumentationScribeValidatedRunOutcome? m3Outcome,
        DocumentationPatchRequest? patchRequest,
        DocumentationPatchExecutionOutcome? patchOutcome)
    {
        Status = status;
        Code = code;
        M3Outcome = m3Outcome;
        PatchRequest = patchRequest;
        PatchOutcome = patchOutcome;
    }

    internal DocumentationScribeCompositionStatus Status { get; }

    internal string Code { get; }

    internal DocumentationScribeValidatedRunOutcome? M3Outcome { get; }

    internal DocumentationScribeRunResult? RunResult => M3Outcome?.RunResult;

    internal DocumentationPatchRequest? PatchRequest { get; }

    internal DocumentationPatchExecutionOutcome? PatchOutcome { get; }

    internal DocumentationPatchAcceptedCandidate? AcceptedCandidate =>
        Status == DocumentationScribeCompositionStatus.PatchAccepted
            ? PatchOutcome?.AcceptedCandidate
            : null;

    internal static DocumentationScribeCompositionOutcome Create(
        DocumentationScribeCompositionStatus status,
        string code,
        DocumentationScribeValidatedRunOutcome? m3Outcome = null,
        DocumentationPatchRequest? patchRequest = null,
        DocumentationPatchExecutionOutcome? patchOutcome = null) =>
        new(status, code, m3Outcome, patchRequest, patchOutcome);

    public override string ToString() => nameof(DocumentationScribeCompositionOutcome);
}

internal interface IDocumentationScribePreparedOutcome
{
    DocumentationScribeCompositionStatus Status { get; }

    string Code { get; }

    DocumentationScribeValidatedRunOutcome? M3Outcome { get; }

    bool IsProposalReady { get; }
}

internal enum DocumentationCampaignPreparationKind
{
    Completion,
    StopCancelled,
    StopTimedOut,
    StopBudgetExhausted,
    Invalid,
}

internal sealed class DocumentationCampaignPreparation
{
    private DocumentationCampaignPreparation(
        DocumentationCampaignPreparationKind kind,
        CampaignProviderCompletionAuthority? completionAuthority)
    {
        Kind = kind;
        CompletionAuthority = completionAuthority;
    }

    internal DocumentationCampaignPreparationKind Kind { get; }
    internal CampaignProviderCompletionAuthority? CompletionAuthority { get; }

    internal static DocumentationCampaignPreparation Completion(CampaignProviderCompletionAuthority authority) =>
        new(DocumentationCampaignPreparationKind.Completion, authority);

    internal static DocumentationCampaignPreparation Stop(DocumentationCampaignPreparationKind kind) =>
        new(kind, null);

    internal static DocumentationCampaignPreparation Invalid() =>
        new(DocumentationCampaignPreparationKind.Invalid, null);

    public override string ToString() => nameof(DocumentationCampaignPreparation);
}

internal sealed class DocumentationScribeAuditAuthority
{
    private readonly ClassifiedRepositorySession session;
    private readonly ObservedRepositorySession observations;
    private readonly ImmutableArray<SelectedRow> rows;
    private readonly ImmutableArray<JsonElement> canonicalRows;

    private DocumentationScribeAuditAuthority(
        ClassifiedRepositorySession session,
        ObservedRepositorySession observations,
        ImmutableArray<SelectedRow> rows,
        ImmutableArray<JsonElement> canonicalRows)
    {
        this.session = session;
        this.observations = observations;
        this.rows = rows;
        this.canonicalRows = canonicalRows;
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

        var providedInputs = acceptedInputs.ToImmutableArray();
        if (providedInputs.IsDefaultOrEmpty || providedInputs.Any(input => input is null))
        {
            throw new ArgumentException("scribe.audit.inputs-invalid", nameof(acceptedInputs));
        }

        var providedDocument = AuditAggregator.Aggregate(
            classifications.TargetProfile,
            classifications,
            acceptedPolicy,
            providedInputs);
        var extraction = new PolicyEvidenceExtractor().Extract(
            session,
            observations,
            acceptedPolicy);
        if (extraction.Status != PolicyEvidenceExtractionStatus.Success)
        {
            throw new ArgumentException("scribe.audit.evidence-unavailable", nameof(observations));
        }

        var inputs = AuditInputAssembler.Assemble(
            classifications,
            acceptedPolicy,
            extraction).ToImmutableArray();
        var derivedDocument = AuditAggregator.Aggregate(
            classifications.TargetProfile,
            classifications,
            acceptedPolicy,
            inputs);
        var acceptedBytes = AuditJson.Write(acceptedDocument);
        var providedBytes = AuditJson.Write(providedDocument);
        var derivedBytes = AuditJson.Write(derivedDocument);
        if (!acceptedBytes.AsSpan().SequenceEqual(providedBytes)
            || !acceptedBytes.AsSpan().SequenceEqual(derivedBytes))
        {
            throw new ArgumentException("scribe.audit.document-mismatch", nameof(acceptedDocument));
        }

        using var parsed = JsonDocument.Parse(acceptedBytes);
        var resultRows = parsed.RootElement.GetProperty("results")
            .EnumerateArray()
            .Select(row => row.Clone())
            .ToImmutableArray();
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
            selected.ToImmutable(),
            resultRows);
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
            canonicalRows,
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
        ImmutableArray<JsonElement> canonicalRows,
        AuditOutcome outcome)
    {
        Session = session;
        Observations = observations;
        Target = target;
        Input = input;
        CanonicalRow = canonicalRow;
        CanonicalRows = canonicalRows;
        Outcome = outcome;
    }

    internal ClassifiedRepositorySession Session { get; }

    internal ObservedRepositorySession Observations { get; }

    internal TargetClassification Target { get; }

    internal TargetAuditInput Input { get; }

    internal JsonElement CanonicalRow { get; }

    internal ImmutableArray<JsonElement> CanonicalRows { get; }

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
    private sealed class PreparedOutcome : IDocumentationScribePreparedOutcome
    {
        private readonly PatchAuthorization? patchAuthorization;
        private int consumptionState;
        private int campaignConsumptionState;

        public PreparedOutcome(
            DocumentationScribeCompositionStatus status,
            string code,
            DocumentationScribeValidatedRunOutcome? m3Outcome,
            PatchAuthorization? patchAuthorization)
        {
            Status = status;
            Code = code;
            M3Outcome = m3Outcome;
            this.patchAuthorization = patchAuthorization;

            var proposalReady = status == DocumentationScribeCompositionStatus.ProposalReady;
            if (proposalReady != (patchAuthorization is not null)
                || proposalReady && m3Outcome?.RunResult.Terminal is not DocumentationScribeProposalTerminal)
            {
                throw new ArgumentException("Patch authorization requires a bound proposal-ready M3 outcome.", nameof(patchAuthorization));
            }
        }

        public DocumentationScribeCompositionStatus Status { get; }

        public string Code { get; }

        public DocumentationScribeValidatedRunOutcome? M3Outcome { get; }

        public bool IsProposalReady => Status == DocumentationScribeCompositionStatus.ProposalReady
            && M3Outcome?.RunResult.Terminal is DocumentationScribeProposalTerminal
            && patchAuthorization is not null;

        public bool TryTakePatchAuthorization(out PatchAuthorization? authorization)
        {
            authorization = Interlocked.CompareExchange(ref consumptionState, 1, 0) == 0
                ? patchAuthorization
                : null;
            return authorization is not null;
        }

        public bool TryTakeCampaignOutcome()
            => Interlocked.CompareExchange(ref campaignConsumptionState, 1, 0) == 0;

        public override string ToString() => nameof(PreparedOutcome);
    }

    private sealed record PatchAuthorization(
        DocumentationScribeSelectedAudit Selection,
        DocumentationPatchResolvedDeclaration Declaration,
        DocumentationPatchEditKind EditKind);

    private sealed class CampaignDispatchExchange(
        CampaignProviderInvocationAuthority invocation,
        IDocumentationScribeModelExchange inner) : IDocumentationScribeModelExchange
    {
        private int state;

        internal bool DispatchStarted => Volatile.Read(ref state) == 2;

        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            var observed = Volatile.Read(ref state);
            if (observed != 2)
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0
                    || !invocation.TryBeginDispatch(out _))
                {
                    throw new InvalidOperationException("scribe.campaign.dispatch-conflict");
                }

                Volatile.Write(ref state, 2);
            }

            return await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static PreparedOutcome CreatePrepared(
        DocumentationScribeCompositionStatus status,
        string code,
        DocumentationScribeValidatedRunOutcome? m3Outcome = null) =>
        new(status, code, m3Outcome, patchAuthorization: null);

    private static PreparedOutcome CreateProposalReady(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeValidatedRunOutcome m3Outcome,
        DocumentationPatchResolvedDeclaration declaration,
        DocumentationPatchEditKind editKind) =>
        new(
            DocumentationScribeCompositionStatus.ProposalReady,
            "scribe.proposal.ready",
            m3Outcome,
            new PatchAuthorization(selection, declaration, editKind));

    private static readonly JsonSerializerOptions ToolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly ReadOnlyMemory<byte> SemanticInputSchema = Encoding.UTF8.GetBytes(
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"description\":\"Preserve this exact value when continuing with a cursor.\"},\"cursor\":{\"type\":\"string\",\"description\":\"Omit on the first call. Use a returned semantic cursor once only with the same operation and page size; never use a repository cursor.\"}}}");

    internal static async Task<DocumentationScribeCompositionOutcome> ExecuteAsync(
        DocumentationScribeSelectedAudit selection,
        ReadOnlyMemory<byte> requestUtf8Json,
        DocumentationScribeAttemptId attemptId,
        string? configuredAgentEntrypoint,
        DocumentationScribeRuntimeOptions runtimeOptions,
        IDocumentationScribeModelExchange exchange,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(
            selection,
            requestUtf8Json,
            attemptId,
            configuredAgentEntrypoint,
            runtimeOptions,
            exchange,
            cancellationToken).ConfigureAwait(false);
        return ConsumePreparedOutcome(selection, prepared, cancellationToken);
    }

    internal static async Task<IDocumentationScribePreparedOutcome> PrepareAsync(
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
                return RejectPrepared("scribe.preflight.request-invalid");
            }

            var preflight = await PreflightAsync(
                selection,
                request,
                attemptId,
                configuredAgentEntrypoint,
                cancellationToken).ConfigureAwait(false);
            if (preflight.Failure is { } failure)
            {
                var mapped = MapPreflightFailure(failure, afterProposal: false);
                return CreatePrepared(mapped.Status, mapped.Code);
            }

            var loaded = preflight.Context!;
            var prompt = BuildPrompt(
                selection,
                request,
                loaded,
                preflight.ContextContent);
            if (prompt is null)
            {
                return RejectPrepared("scribe.preflight.prompt-evidence-mismatch");
            }

            var registry = BuildRegistry(
                request,
                loaded,
                preflight.SourceReference!,
                preflight.Repository!,
                preflight.RepositoryScopes);
            var runtime = new DocumentationScribeRuntime(exchange, registry, runtimeOptions);
            var run = await runtime.RunAsync(
                request,
                attemptId,
                prompt,
                cancellationToken).ConfigureAwait(false);
            DocumentationScribeValidatedRunOutcome bound;
            try
            {
                bound = DocumentationScribeValidation.BindValidatedRunOutcome(request, attemptId, run);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return CreatePrepared(
                    DocumentationScribeCompositionStatus.ProposalRejected,
                    "scribe.proposal.correlation-invalid");
            }

            return await AuthorizeProposalAsync(
                selection,
                attemptId,
                configuredAgentEntrypoint,
                loaded,
                bound,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreatePrepared(
                DocumentationScribeCompositionStatus.Cancelled,
                "scribe.cancelled.caller");
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return CreatePrepared(
                DocumentationScribeCompositionStatus.RuntimeFailure,
                "scribe.failure.internal");
        }
    }

    internal static async Task<DocumentationCampaignPreparation> PrepareCampaignAsync(
        DocumentationScribeSelectedAudit selection,
        ReadOnlyMemory<byte> requestUtf8Json,
        CampaignProviderInvocationAuthority invocation,
        string? configuredAgentEntrypoint,
        DocumentationScribeRuntimeOptions runtimeOptions,
        IDocumentationScribeModelExchange exchange,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentNullException.ThrowIfNull(exchange);
        var parsed = DocumentationScribeValidation.ParseRequest(requestUtf8Json);
        if (!parsed.IsValid || parsed.Request is not { } request)
        {
            return DocumentationCampaignPreparation.Invalid();
        }

        var registrar = invocation.TryCreateCompletionRegistrar();
        if (registrar is null
            || !registrar.TryAuthorizePreparation(
                request,
                runtimeOptions.ProviderConfigurationId,
                runtimeOptions.ModelConfigurationId,
                runtimeOptions.ScribeProtocolId,
                out var attemptId))
        {
            return DocumentationCampaignPreparation.Invalid();
        }

        var clock = timeProvider ?? TimeProvider.System;
        var gated = new CampaignDispatchExchange(invocation, exchange);
        long started;
        try
        {
            started = clock.GetTimestamp();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return DocumentationCampaignPreparation.Invalid();
        }
        var prepared = await PrepareAsync(
            selection,
            requestUtf8Json,
            attemptId,
            configuredAgentEntrypoint,
            runtimeOptions,
            gated,
            cancellationToken).ConfigureAwait(false);
        double elapsed;
        try
        {
            elapsed = clock.GetElapsedTime(started, clock.GetTimestamp()).TotalMilliseconds;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return DocumentationCampaignPreparation.Invalid();
        }
        if (double.IsNaN(elapsed)
            || elapsed < 0
            || elapsed > CampaignStateContract.MaximumObservation)
        {
            return DocumentationCampaignPreparation.Invalid();
        }

        if (prepared is not PreparedOutcome owned || !owned.TryTakeCampaignOutcome())
        {
            return DocumentationCampaignPreparation.Invalid();
        }

        var m3 = owned.M3Outcome;
        if (m3 is null && !gated.DispatchStarted)
        {
            var stop = owned.Status switch
            {
                DocumentationScribeCompositionStatus.Cancelled => DocumentationCampaignPreparationKind.StopCancelled,
                DocumentationScribeCompositionStatus.Timeout => DocumentationCampaignPreparationKind.StopTimedOut,
                DocumentationScribeCompositionStatus.BudgetExhausted => DocumentationCampaignPreparationKind.StopBudgetExhausted,
                _ => DocumentationCampaignPreparationKind.Invalid,
            };
            if (stop != DocumentationCampaignPreparationKind.Invalid)
            {
                return DocumentationCampaignPreparation.Stop(stop);
            }
        }

        var kind = m3?.RunResult.Terminal is not DocumentationScribeProposalTerminal
            ? CampaignProviderCompletionKind.Ordinary
            : owned.Status switch
            {
                DocumentationScribeCompositionStatus.ProposalReady => CampaignProviderCompletionKind.Ordinary,
                DocumentationScribeCompositionStatus.Cancelled => CampaignProviderCompletionKind.CallerCancelled,
                DocumentationScribeCompositionStatus.Timeout => CampaignProviderCompletionKind.Timeout,
                DocumentationScribeCompositionStatus.BudgetExhausted => CampaignProviderCompletionKind.BudgetExhausted,
                DocumentationScribeCompositionStatus.RuntimeFailure => CampaignProviderCompletionKind.HostFailure,
                _ => CampaignProviderCompletionKind.ProposalInvalid,
            };
        if (m3 is null)
        {
            kind = owned.Status switch
            {
                DocumentationScribeCompositionStatus.RuntimeFailure => CampaignProviderCompletionKind.HostFailure,
                DocumentationScribeCompositionStatus.Cancelled => CampaignProviderCompletionKind.CallerCancelled,
                DocumentationScribeCompositionStatus.Timeout => CampaignProviderCompletionKind.Timeout,
                DocumentationScribeCompositionStatus.BudgetExhausted => CampaignProviderCompletionKind.BudgetExhausted,
                _ => CampaignProviderCompletionKind.ProposalInvalid,
            };
        }

        long? hostElapsed = m3 is null ? null : checked((long)Math.Ceiling(elapsed));
        return registrar.TryRegister(kind, m3, hostElapsed, out var authority)
            && authority is not null
            ? DocumentationCampaignPreparation.Completion(authority)
            : DocumentationCampaignPreparation.Invalid();
    }

    private static async Task<PreparedOutcome> AuthorizeProposalAsync(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeAttemptId attemptId,
        string? configuredAgentEntrypoint,
        DocumentationScribeLoadedContext loaded,
        DocumentationScribeValidatedRunOutcome bound,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = bound.RunResult;
            if (run.Terminal is DocumentationScribeCancelledTerminal cancelled)
            {
                return CreatePrepared(
                    DocumentationScribeCompositionStatus.Cancelled,
                    DocumentationScribeVocabulary.GetId(cancelled.Code),
                    bound);
            }

            if (run.Terminal is DocumentationScribeFailureTerminal terminalFailure)
            {
                var status = terminalFailure.Code switch
                {
                    DocumentationScribeFailureCode.Provider => DocumentationScribeCompositionStatus.ProviderFailure,
                    DocumentationScribeFailureCode.Timeout => DocumentationScribeCompositionStatus.Timeout,
                    DocumentationScribeFailureCode.Budget => DocumentationScribeCompositionStatus.BudgetExhausted,
                    _ => DocumentationScribeCompositionStatus.RuntimeFailure,
                };
                return CreatePrepared(
                    status,
                    DocumentationScribeVocabulary.GetId(terminalFailure.Code),
                    bound);
            }

            if (run.Terminal is DocumentationScribeSkipTerminal)
            {
                return CreatePrepared(
                    DocumentationScribeCompositionStatus.ProposalSkipped,
                    "scribe.proposal.skipped",
                    bound);
            }

            if (run.Terminal is not DocumentationScribeProposalTerminal proposal)
            {
                return CreatePrepared(
                    DocumentationScribeCompositionStatus.ProposalRejected,
                    "scribe.proposal.correlation-invalid",
                    bound);
            }

            var postflight = await PreflightAsync(
                selection,
                bound.Request,
                attemptId,
                configuredAgentEntrypoint,
                cancellationToken).ConfigureAwait(false);
            if (postflight.Failure is { } postflightFailure)
            {
                var outcome = MapPreflightFailure(postflightFailure, afterProposal: true);
                return CreatePrepared(outcome.Status, outcome.Code, bound);
            }

            if (!loaded.VerifyFreshness(cancellationToken))
            {
                return CreatePrepared(
                    DocumentationScribeCompositionStatus.PatchStale,
                    "scribe.patch.stale-context",
                    bound);
            }

            return CreateProposalReady(
                selection,
                bound,
                postflight.Declaration!,
                postflight.EditKind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreatePrepared(
                DocumentationScribeCompositionStatus.Cancelled,
                "scribe.cancelled.caller",
                bound);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return CreatePrepared(
                DocumentationScribeCompositionStatus.RuntimeFailure,
                "scribe.failure.internal",
                bound);
        }
    }

    internal static DocumentationScribeCompositionOutcome ConsumePreparedOutcome(
        DocumentationScribeSelectedAudit selection,
        IDocumentationScribePreparedOutcome prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared is not PreparedOutcome owned)
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.ProposalRejected,
                "scribe.proposal.foreign-prepared");
        }

        if (!owned.IsProposalReady)
        {
            return DocumentationScribeCompositionOutcome.Create(
                owned.Status,
                owned.Code,
                owned.M3Outcome);
        }

        if (!owned.TryTakePatchAuthorization(out var authorization)
            || authorization is null
            || owned.M3Outcome?.RunResult.Terminal is not DocumentationScribeProposalTerminal proposal)
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.ProposalRejected,
                "scribe.proposal.already-consumed",
                owned.M3Outcome);
        }

        try
        {
            if (!ReferenceEquals(selection, authorization.Selection)
                || !IsPreparedAuthorizationCurrent(selection, owned.M3Outcome.Request, authorization, cancellationToken))
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.PatchStale,
                    "scribe.patch.prepared-authority-mismatch",
                    owned.M3Outcome);
            }

            var patchBytes = BuildPatchRequest(
                owned.M3Outcome.Request,
                authorization.Declaration,
                authorization.EditKind,
                proposal.PatchContent,
                proposal.ContentUnits);
            var patchParse = DocumentationPatchValidator.ParseRequest(patchBytes);
            if (!patchParse.IsValid || patchParse.Request is not { } patchRequest)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.ProposalRejected,
                    "scribe.patch.request-invalid",
                    owned.M3Outcome);
            }

            var patchOutcome = new DocumentationPatchEngine().Execute(
                selection.Session,
                patchRequest,
                cancellationToken);
            if (patchOutcome.Status == DocumentationPatchExecutionStatus.HostFailure)
            {
                return DocumentationScribeCompositionOutcome.Create(
                    DocumentationScribeCompositionStatus.RuntimeFailure,
                    patchOutcome.FailureCode ?? "scribe.patch.host-failure",
                    owned.M3Outcome,
                    patchRequest,
                    patchOutcome);
            }

            var patchStatus = patchOutcome.Result?.Outcome switch
            {
                DocumentationPatchOutcome.Accepted when patchOutcome.AcceptedCandidate is not null =>
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
                owned.M3Outcome,
                patchRequest,
                patchOutcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.Cancelled,
                "scribe.cancelled.caller",
                owned.M3Outcome);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return DocumentationScribeCompositionOutcome.Create(
                DocumentationScribeCompositionStatus.RuntimeFailure,
                "scribe.failure.internal",
                owned.M3Outcome);
        }
    }

    private static bool IsPreparedAuthorizationCurrent(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeRequest request,
        PatchAuthorization authorization,
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
            || authorization.Declaration.RepositoryPath
                is not { } repositoryPath
            || authorization.Declaration.SourceSha256 != request.Target.SourceSha256
            || request.Target.SourceLocator is not RepositoryEvidenceLocator locator
            || locator.Path != repositoryPath
            || locator.Span is null
            || authorization.Declaration.RequestedDeclarationSpan != locator.Span.Value
            || !ComponentsEqual(
                request.Target.ApplicableComponents,
                authorization.Declaration.ApplicableComponents))
        {
            return false;
        }

        var capture = selection.Session.RepositorySession
            .CaptureDocumentationPatchResolutionBaseline(cancellationToken);
        return capture.Baseline is { } baseline
            && baseline.TryGetEntry(repositoryPath, out var entry)
            && string.Equals(entry.Sha256, request.Target.SourceSha256, StringComparison.Ordinal);
    }

    private static async Task<PreflightResult> PreflightAsync(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
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
            var failureKind = bootstrap.Status switch
            {
                DocumentationScribeContextBootstrapStatus.Cancelled => PreflightFailureKind.Cancelled,
                DocumentationScribeContextBootstrapStatus.TimedOut => PreflightFailureKind.TimedOut,
                DocumentationScribeContextBootstrapStatus.BudgetExhausted => PreflightFailureKind.BudgetExhausted,
                _ when bootstrap.Failure?.Category == DocumentationScribeContextFailureCategory.Internal =>
                    PreflightFailureKind.Internal,
                _ => PreflightFailureKind.Rejected,
            };
            var code = bootstrap.Status switch
            {
                DocumentationScribeContextBootstrapStatus.Cancelled => "scribe.cancelled.caller",
                DocumentationScribeContextBootstrapStatus.TimedOut => "scribe.failure.timeout",
                DocumentationScribeContextBootstrapStatus.BudgetExhausted => "scribe.failure.budget",
                _ => bootstrap.Failure?.Code ?? "scribe.preflight.context-unavailable",
            };
            return PreflightResult.Failed(failureKind, code);
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

        // Start the shared repository-tool session only after patch authorization so
        // unrelated baseline capture and declaration resolution cannot consume its timer.
        var scopes = BuildRepositoryScopes(request, context, sourceReference);
        var nonInstructionContextCount = request.ContextReferences.Count(reference =>
            reference.Kind != DocumentationScribeContextReferenceKind.ProjectInstruction);
        var repositoryLimits = DocumentationScribeRepositoryToolLimits.Create(
            maximumBytesReadPerRun: 67_108_864,
            maximumReturnedUtf8BytesPerRun: DocumentationScribeContract.MaximumArtifactUtf8Bytes,
            maximumCallsPerOperation: checked(
                Math.Max(1, request.Limits.MaximumToolCalls + nonInstructionContextCount)));
        var repository = DocumentationScribeRepositoryToolBundle.Create(
            request,
            attemptId,
            context,
            scopes,
            repositoryLimits);
        var contextMaterialization = await MaterializeContextContentAsync(
            request,
            context,
            repository,
            cancellationToken).ConfigureAwait(false);
        if (contextMaterialization.Failure is { } contextFailure)
        {
            return PreflightResult.Failed(contextFailure.Kind, contextFailure.Code);
        }

        var contextContent = contextMaterialization.Content;

        return new PreflightResult(
            context,
            declaration,
            editKind.Value,
            sourceReference,
            contextContent,
            repository,
            scopes,
            null);
    }

    private static DocumentationScribePromptInput? BuildPrompt(
        DocumentationScribeSelectedAudit selection,
        DocumentationScribeRequest request,
        DocumentationScribeLoadedContext context,
        ImmutableArray<BoundContextContent> contextContentSources)
    {
        var contextContent = ImmutableArray.CreateBuilder<DocumentationScribeContextContent>();
        foreach (var reference in request.ContextReferences)
        {
            var sources = contextContentSources.Where(candidate =>
                BoundContextMatches(candidate, reference)).ToArray();
            if (sources.Length != 1)
            {
                return null;
            }

            var source = sources[0];

            contextContent.Add(new DocumentationScribeContextContent(
                reference.ContextReferenceId,
                reference.Kind,
                reference.ContentSha256,
                reference.IncludedUtf8ByteCount,
                reference.IsTruncated,
                source.Content));
        }

        var evidenceContent = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceContent>();
        foreach (var reference in request.EvidenceReferences)
        {
            var loadedFacts = context.Facts.Evidence
                .Where(candidate => ContextEvidenceMatches(candidate, reference))
                .ToArray();
            if (loadedFacts.Length > 1)
            {
                return null;
            }

            var content = loadedFacts.Length == 1
                ? loadedFacts[0].Content
                : AuditEvidenceContent(selection.CanonicalRows, reference);
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

    private static async ValueTask<ContextMaterializationResult> MaterializeContextContentAsync(
        DocumentationScribeRequest request,
        DocumentationScribeLoadedContext context,
        DocumentationScribeRepositoryToolBundle repository,
        CancellationToken cancellationToken)
    {
        var content = ImmutableArray.CreateBuilder<BoundContextContent>();
        foreach (var reference in request.ContextReferences)
        {
            if (reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction)
            {
                var instructions = context.Facts.Instructions.Where(candidate =>
                    candidate.InstructionId == reference.ContextReferenceId
                    && ContextCommitmentMatches(candidate.Commitment, reference)).ToArray();
                if (instructions.Length != 1)
                {
                    return ContextMaterializationResult.Rejected();
                }

                content.Add(BindContextContent(reference, instructions[0].Role, instructions[0].Content));
                continue;
            }

            if (context.Facts.Instructions.Any(candidate =>
                    candidate.Commitment.RepositoryPath == reference.Path)
                || context.Facts.Evidence.Any(candidate =>
                    candidate.Commitment.RepositoryPath == reference.Path))
            {
                return ContextMaterializationResult.Rejected();
            }

            var result = await repository.ReadExcerpt.InvokeAsync(
                new DocumentationScribeRepositoryReadExcerptRequest(
                    reference.ContextReferenceId,
                    reference.Path),
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == DocumentationScribeToolOutcome.BudgetExhausted)
            {
                return ContextMaterializationResult.Failed(
                    PreflightFailureKind.BudgetExhausted,
                    "scribe.failure.budget");
            }

            if (result.Outcome == DocumentationScribeToolOutcome.TimedOut)
            {
                return ContextMaterializationResult.Failed(
                    PreflightFailureKind.TimedOut,
                    "scribe.failure.timeout");
            }

            if (result.Outcome == DocumentationScribeToolOutcome.Cancelled)
            {
                return ContextMaterializationResult.Failed(
                    PreflightFailureKind.Cancelled,
                    "scribe.cancelled.caller");
            }

            var excerpt = result.Excerpt;
            if (excerpt is null
                || !result.DynamicEvidence.IsEmpty
                || result.Route is not null
                || excerpt.RepositoryPath != reference.Path
                || excerpt.ContentSha256 != reference.ContentSha256
                || excerpt.OriginalUtf8ByteCount != reference.OriginalUtf8ByteCount
                || excerpt.IncludedUtf8ByteCount != reference.IncludedUtf8ByteCount
                || excerpt.IsTruncated != reference.IsTruncated)
            {
                return ContextMaterializationResult.Rejected();
            }

            content.Add(BindContextContent(
                reference,
                DocumentationScribeContextRole.MaintainedDocumentation,
                excerpt.Content));
        }

        return ContextMaterializationResult.Accepted(content.ToImmutable());
    }

    private static BoundContextContent BindContextContent(
        DocumentationScribeContextReference reference,
        DocumentationScribeContextRole role,
        string content) =>
        new(
            reference.ContextReferenceId,
            reference.Kind,
            role,
            reference.Path,
            reference.ContentSha256,
            reference.OriginalUtf8ByteCount,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            content);

    private static bool BoundContextMatches(
        BoundContextContent content,
        DocumentationScribeContextReference reference) =>
        content.ContextReferenceId == reference.ContextReferenceId
        && content.Kind == reference.Kind
        && content.RepositoryPath == reference.Path
        && content.ContentSha256 == reference.ContentSha256
        && content.OriginalUtf8ByteCount == reference.OriginalUtf8ByteCount
        && content.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
        && content.IsTruncated == reference.IsTruncated;

    private static bool ContextCommitmentMatches(
        DocumentationScribeContextSourceCommitment commitment,
        DocumentationScribeContextReference reference) =>
        commitment.RepositoryPath == reference.Path
        && commitment.ContentSha256 == reference.ContentSha256
        && commitment.OriginalUtf8ByteCount == reference.OriginalUtf8ByteCount
        && commitment.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
        && commitment.IsTruncated == reference.IsTruncated;

    private static string? AuditEvidenceContent(
        ImmutableArray<JsonElement> rows,
        DocumentationScribeEvidenceReference reference)
    {
        var matching = new List<JsonElement>();
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("evidenceBundle", out var bundle)
                || !bundle.TryGetProperty("items", out var items))
            {
                continue;
            }

            matching.AddRange(items.EnumerateArray().Where(item =>
                AuditEvidenceMatches(item, reference)));
        }

        return matching.Count == 1 ? matching[0].GetProperty("excerpt").GetString() : null;
    }

    private static bool ContextEvidenceMatches(
        DocumentationScribeEvidenceContextFact fact,
        DocumentationScribeEvidenceReference reference)
    {
        var expectedSubjectId = reference.Subject is TargetEvidenceSubject target
            ? "symbol." + DocumentationScribeContextValidation.ComputeSymbolRefSha256(
                target.ParentSymbolRef)
            : null;
        return expectedSubjectId is not null
            && fact.SubjectId == expectedSubjectId
            && fact.Authority == DocumentationScribeContextAuthority.Source
            && fact.Role == DocumentationScribeContextRole.SourceDeclaration
            && fact.KindId == "source.target-declaration"
            && reference.Kind == EvidenceKind.SourceDeclaration
            && reference.Relation == EvidenceRelation.Declares
            && reference.Authority == DocumentationScribeEvidenceAuthority.SourceDeclaration
            && Equals(fact.Commitment.Locator, reference.Locator)
            && fact.Commitment.ContentSha256 == reference.ContentSha256
            && fact.Commitment.OriginalUtf8ByteCount == reference.OriginalUtf8ByteCount
            && fact.Commitment.IncludedUtf8ByteCount == reference.IncludedUtf8ByteCount
            && fact.Commitment.IsTruncated == reference.IsTruncated;
    }

    private static bool AuditEvidenceMatches(
        JsonElement item,
        DocumentationScribeEvidenceReference reference) =>
        item.GetProperty("evidenceId").GetString() == reference.EvidenceReferenceId
        && item.GetProperty("kind").GetString() == EvidenceVocabulary.GetId(reference.Kind)
        && item.GetProperty("relation").GetString() == EvidenceVocabulary.GetId(reference.Relation)
        && AuthorityMatchesKind(reference.Authority, reference.Kind)
        && AuditSubjectMatches(item.GetProperty("subject"), reference.Subject)
        && AuditLocatorMatches(item.GetProperty("locator"), reference.Locator)
        && item.GetProperty("sha256").GetString() == reference.ContentSha256
        && item.GetProperty("originalUtf8ByteCount").GetInt32()
            == reference.OriginalUtf8ByteCount
        && item.GetProperty("includedUtf8ByteCount").GetInt32()
            == reference.IncludedUtf8ByteCount
        && item.GetProperty("isTruncated").GetBoolean() == reference.IsTruncated;

    private static bool AuditSubjectMatches(JsonElement item, EvidenceSubject subject) => subject switch
    {
        TargetEvidenceSubject target => SymbolMatches(item, target.ParentSymbolRef),
        ComponentEvidenceSubject component =>
            item.TryGetProperty("parentSymbolRef", out var parent)
            && SymbolMatches(parent, component.ParentSymbolRef)
            && item.GetProperty("componentKind").GetString()
                == ClassificationVocabulary.GetId(component.ComponentKind)
            && item.GetProperty("identity").GetString() == component.Identity,
        _ => false,
    };

    private static bool SymbolMatches(JsonElement item, SymbolRef symbol) =>
        item.TryGetProperty("compilationContextRef", out var context)
        && context.GetString() == symbol.CompilationContextRef
        && item.TryGetProperty("documentationCommentId", out var documentationId)
        && documentationId.GetString() == symbol.DocumentationCommentId;

    private static bool AuditLocatorMatches(JsonElement item, EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository =>
            item.TryGetProperty("repository", out var value)
            && value.GetProperty("path").GetString() == repository.Path
            && SpanMatches(value, repository.Span),
        GeneratedOutputEvidenceLocator generated =>
            item.TryGetProperty("generatedOutput", out var value)
            && value.GetProperty("producerKind").GetString()
                == PolicyConfigurationVocabulary.GetId(generated.ProducerKind)
            && value.GetProperty("producerId").GetString() == generated.ProducerId
            && value.GetProperty("outputId").GetString() == generated.OutputId
            && value.GetProperty("sourceSha256").GetString() == generated.SourceSha256
            && SpanMatches(value, generated.Span),
        MetadataEvidenceLocator metadata =>
            item.TryGetProperty("metadata", out var value)
            && value.GetProperty("assemblyIdentity").GetString() == metadata.AssemblyIdentity
            && value.GetProperty("documentationCommentId").GetString()
                == metadata.DocumentationCommentId,
        SyntheticEvidenceLocator synthetic =>
            item.TryGetProperty("synthetic", out var value)
            && value.GetProperty("fixtureId").GetString() == synthetic.FixtureId,
        _ => false,
    };

    private static bool SpanMatches(JsonElement item, Utf16Span? span) => span is { } expected
        ? item.TryGetProperty("span", out var value)
            && value.GetProperty("start").GetInt32() == expected.Start
            && value.GetProperty("end").GetInt32() == expected.End
        : !item.TryGetProperty("span", out _);

    private static bool AuthorityMatchesKind(
        DocumentationScribeEvidenceAuthority authority,
        EvidenceKind kind) => (authority, kind) switch
        {
            (DocumentationScribeEvidenceAuthority.PublicContract, EvidenceKind.PublicContract) => true,
            (DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
                EvidenceKind.RepositoryDocumentation) => true,
            (DocumentationScribeEvidenceAuthority.SourceDeclaration, EvidenceKind.SourceDeclaration) => true,
            (DocumentationScribeEvidenceAuthority.SourceDeclaration, EvidenceKind.SourceAttribute) => true,
            (DocumentationScribeEvidenceAuthority.SourceImplementation,
                EvidenceKind.SourceImplementation) => true,
            (DocumentationScribeEvidenceAuthority.ExistingDocumentation,
                EvidenceKind.SourceXmlDocumentation) => true,
            (DocumentationScribeEvidenceAuthority.Test, EvidenceKind.Test) => true,
            _ => false,
        };

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

    private static ImmutableArray<DocumentationScribeRepositoryToolScope> BuildRepositoryScopes(
        DocumentationScribeRequest request,
        DocumentationScribeLoadedContext context,
        DocumentationScribeEvidenceReference sourceReference)
    {
        var locator = (RepositoryEvidenceLocator)request.Target.SourceLocator;
        var requiresCompleteEvidence = sourceReference.ClaimCategoryIds.Any(category =>
            request.StyleProfile.ClaimPolicies.Single(policy =>
                policy.ClaimCategoryId == category).CompleteEvidenceRequired);
        var sourceOperations = DocumentationScribeRepositoryToolOperations.ReadExcerpt
            | DocumentationScribeRepositoryToolOperations.SearchText;
        var scopes = ImmutableArray.CreateBuilder<DocumentationScribeRepositoryToolScope>();
        scopes.Add(requiresCompleteEvidence
            ? DocumentationScribeRepositoryToolScope.File(
                sourceReference.EvidenceReferenceId,
                locator.Path,
                sourceOperations,
                DocumentationScribeContextRole.SourceDeclaration)
            : DocumentationScribeRepositoryToolScope.File(
                sourceReference.EvidenceReferenceId,
                locator.Path,
                sourceOperations,
                DocumentationScribeContextRole.SourceDeclaration,
                subject: sourceReference.Subject,
                kind: sourceReference.Kind,
                relation: sourceReference.Relation,
                authority: sourceReference.Authority,
                claimCategoryIds: sourceReference.ClaimCategoryIds));
        foreach (var reference in request.ContextReferences)
        {
            if (reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction)
            {
                var separator = reference.Path.LastIndexOf('/');
                var directory = separator < 0 ? string.Empty : reference.Path[..separator];
                scopes.Add(DocumentationScribeRepositoryToolScope.Directory(
                    reference.ContextReferenceId,
                    directory,
                    DocumentationScribeRepositoryToolOperations.ReadExcerpt
                        | (context.Facts.Routes.Length > 0
                            ? DocumentationScribeRepositoryToolOperations.ListFiles
                            : DocumentationScribeRepositoryToolOperations.None)
                        | DocumentationScribeRepositoryToolOperations.SearchText,
                    DocumentationScribeContextRole.MaintainedDocumentation,
                    extensions: [".md"]));
            }
            else
            {
                scopes.Add(DocumentationScribeRepositoryToolScope.File(
                    reference.ContextReferenceId,
                    reference.Path,
                    DocumentationScribeRepositoryToolOperations.ReadExcerpt
                        | DocumentationScribeRepositoryToolOperations.SearchText,
                    DocumentationScribeContextRole.MaintainedDocumentation));
            }
        }

        return scopes.ToImmutable();
    }

    private static DocumentationScribeToolRegistry BuildRegistry(
        DocumentationScribeRequest request,
        DocumentationScribeLoadedContext context,
        DocumentationScribeEvidenceReference sourceReference,
        DocumentationScribeRepositoryToolBundle repository,
        ImmutableArray<DocumentationScribeRepositoryToolScope> scopes)
    {
        var maximumCallsPerOperation = Math.Max(1, request.Limits.MaximumToolCalls);
        var builder = new DocumentationScribeToolRegistryBuilder(request.ToolPolicyId)
            .Add(
                DocumentationScribeRepositoryToolBundle.ReadExcerptDescriptor,
                repository.ReadExcerpt,
                new ReadCodec(sourceReference),
                DocumentationScribeRepositoryToolSchemas.ReadExcerptDescription,
                BindRepositoryToolScopes(
                    DocumentationScribeRepositoryToolSchemas.ReadExcerptInputUtf8Json,
                    scopes,
                    DocumentationScribeRepositoryToolOperations.ReadExcerpt),
                maximumCallsPerRun: maximumCallsPerOperation);
        if (context.Facts.Routes.Length > 0)
        {
            builder.Add(
                DocumentationScribeRepositoryToolBundle.ListFilesDescriptor,
                repository.ListFiles,
                new ListCodec(),
                DocumentationScribeRepositoryToolSchemas.ListFilesDescription,
                BindRepositoryToolScopes(
                    DocumentationScribeRepositoryToolSchemas.ListFilesInputUtf8Json,
                    scopes,
                    DocumentationScribeRepositoryToolOperations.ListFiles),
                maximumCallsPerRun: maximumCallsPerOperation);
        }

        builder.Add(
                DocumentationScribeRepositoryToolBundle.SearchTextDescriptor,
                repository.SearchText,
                new SearchCodec(sourceReference),
                DocumentationScribeRepositoryToolSchemas.SearchTextDescription,
                BindRepositoryToolScopes(
                    DocumentationScribeRepositoryToolSchemas.SearchTextInputUtf8Json,
                    scopes,
                    DocumentationScribeRepositoryToolOperations.SearchText),
                maximumCallsPerRun: maximumCallsPerOperation)
            .Add(
                new DocumentationScribeSemanticToolDescriptor(),
                new DocumentationScribeSemanticToolPort(context, request),
                new SemanticCodec(),
                "Read one bounded semantic evidence page for the exact selected method.",
                SemanticInputSchema,
                maximumCallsPerRun: maximumCallsPerOperation);
        return builder.Build();
    }

    internal static ReadOnlyMemory<byte> BindRepositoryToolScopes(
        ReadOnlyMemory<byte> inputSchemaUtf8Json,
        IEnumerable<DocumentationScribeRepositoryToolScope> scopes,
        DocumentationScribeRepositoryToolOperations operation)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (operation is not (DocumentationScribeRepositoryToolOperations.ReadExcerpt
            or DocumentationScribeRepositoryToolOperations.ListFiles
            or DocumentationScribeRepositoryToolOperations.SearchText))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        var scopeIds = scopes
            .Where(scope => (scope.Operations & operation) != 0)
            .Select(scope => scope.ScopeId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (scopeIds.Length == 0)
        {
            throw new ArgumentException("The tool has no authorized repository scope.", nameof(scopes));
        }

        var schema = JsonNode.Parse(inputSchemaUtf8Json.Span)?.AsObject()
            ?? throw new ArgumentException("The repository tool schema is not an object.", nameof(inputSchemaUtf8Json));
        var scopeProperty = schema["properties"]?["scopeId"]?.AsObject()
            ?? throw new ArgumentException("The repository tool schema has no scope property.", nameof(inputSchemaUtf8Json));
        var allowed = new JsonArray();
        foreach (var scopeId in scopeIds)
        {
            allowed.Add(scopeId);
        }

        scopeProperty["enum"] = allowed;
        return JsonSerializer.SerializeToUtf8Bytes(schema);
    }

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
            var value = new JsonObject
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
            };
            if (component.Name is { } name)
            {
                value["name"] = name;
            }

            result.Add(value);
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

    private static PreparedOutcome RejectPrepared(string code) =>
        CreatePrepared(
            DocumentationScribeCompositionStatus.PreflightRejected,
            code);

    private static DocumentationScribeCompositionOutcome MapPreflightFailure(
        PreflightFailure failure,
        bool afterProposal)
    {
        var status = failure.Kind switch
        {
            PreflightFailureKind.Rejected when afterProposal =>
                DocumentationScribeCompositionStatus.PatchStale,
            PreflightFailureKind.Rejected => DocumentationScribeCompositionStatus.PreflightRejected,
            PreflightFailureKind.Cancelled => DocumentationScribeCompositionStatus.Cancelled,
            PreflightFailureKind.TimedOut => DocumentationScribeCompositionStatus.Timeout,
            PreflightFailureKind.BudgetExhausted =>
                DocumentationScribeCompositionStatus.BudgetExhausted,
            _ => DocumentationScribeCompositionStatus.RuntimeFailure,
        };
        return DocumentationScribeCompositionOutcome.Create(status, failure.Code);
    }

    private enum PreflightFailureKind
    {
        Rejected,
        Cancelled,
        TimedOut,
        BudgetExhausted,
        Internal,
    }

    private sealed record PreflightFailure(PreflightFailureKind Kind, string Code);

    private sealed record BoundContextContent(
        string ContextReferenceId,
        DocumentationScribeContextReferenceKind Kind,
        DocumentationScribeContextRole Role,
        string RepositoryPath,
        string ContentSha256,
        int OriginalUtf8ByteCount,
        int IncludedUtf8ByteCount,
        bool IsTruncated,
        string Content);

    private sealed record ContextMaterializationResult(
        ImmutableArray<BoundContextContent> Content,
        PreflightFailure? Failure)
    {
        internal static ContextMaterializationResult Accepted(
            ImmutableArray<BoundContextContent> content) =>
            new(content, null);

        internal static ContextMaterializationResult Rejected() =>
            Failed(
                PreflightFailureKind.Rejected,
                "scribe.preflight.prompt-evidence-mismatch");

        internal static ContextMaterializationResult Failed(
            PreflightFailureKind kind,
            string code) =>
            new([], new PreflightFailure(kind, code));
    }

    private sealed record PreflightResult(
        DocumentationScribeLoadedContext? Context,
        DocumentationPatchResolvedDeclaration? Declaration,
        DocumentationPatchEditKind EditKind,
        DocumentationScribeEvidenceReference? SourceReference,
        ImmutableArray<BoundContextContent> ContextContent,
        DocumentationScribeRepositoryToolBundle? Repository,
        ImmutableArray<DocumentationScribeRepositoryToolScope> RepositoryScopes,
        PreflightFailure? Failure)
    {
        internal static PreflightResult Rejected(string code) =>
            Failed(PreflightFailureKind.Rejected, code);

        internal static PreflightResult Failed(PreflightFailureKind kind, string code) =>
            new(null, null, default, null, [], null, [], new PreflightFailure(kind, code));
    }

    private abstract class RepositoryCodec<TRequest, TResult>
        : IDocumentationScribeToolCodec<TRequest, TResult>
        where TRequest : IDocumentationScribeToolRequest<TResult>
        where TResult : IDocumentationScribeToolResult
    {
        private readonly DocumentationScribeEvidenceReference? authorizedSource;

        protected RepositoryCodec(DocumentationScribeEvidenceReference? authorizedSource = null) =>
            this.authorizedSource = authorizedSource;

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
            if (authorizedSource is { } source)
            {
                evidence = evidence.Where(item => IsAuthorizedSourceRange(item, source)).ToImmutableArray();
            }
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

        private static bool IsAuthorizedSourceRange(
            DocumentationScribeDynamicEvidenceInput item,
            DocumentationScribeEvidenceReference source) =>
            Equals(item.Subject, source.Subject)
            && item.Kind == source.Kind
            && item.Relation == source.Relation
            && item.Authority == source.Authority
            && item.ClaimCategoryIds.SequenceEqual(source.ClaimCategoryIds, StringComparer.Ordinal)
            && item.Locator is RepositoryEvidenceLocator itemLocator
            && source.Locator is RepositoryEvidenceLocator sourceLocator
            && itemLocator.Path == sourceLocator.Path
            && itemLocator.Span is { } itemSpan
            && sourceLocator.Span is { } sourceSpan
            && itemSpan.Start >= sourceSpan.Start
            && itemSpan.End <= sourceSpan.End;
    }

    private sealed class ReadCodec
        : RepositoryCodec<DocumentationScribeRepositoryReadExcerptRequest, DocumentationScribeRepositoryReadExcerptResult>
    {
        internal ReadCodec(DocumentationScribeEvidenceReference authorizedSource)
            : base(authorizedSource)
        {
        }

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
        internal SearchCodec(DocumentationScribeEvidenceReference authorizedSource)
            : base(authorizedSource)
        {
        }

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
