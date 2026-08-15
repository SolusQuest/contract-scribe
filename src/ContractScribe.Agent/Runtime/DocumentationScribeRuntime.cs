using System.Collections.Immutable;
using System.Text;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

public sealed class DocumentationScribeRuntimeOptions
{
    public DocumentationScribeRuntimeOptions(
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId)
    {
        ProviderConfigurationId = DocumentationScribeBoundary.ValidateIdentifier(
            providerConfigurationId,
            nameof(providerConfigurationId));
        ModelConfigurationId = DocumentationScribeBoundary.ValidateIdentifier(
            modelConfigurationId,
            nameof(modelConfigurationId));
        ScribeProtocolId = DocumentationScribeBoundary.ValidateIdentifier(
            scribeProtocolId,
            nameof(scribeProtocolId));
    }

    public string ProviderConfigurationId { get; }

    public string ModelConfigurationId { get; }

    public string ScribeProtocolId { get; }

    public override string ToString() => nameof(DocumentationScribeRuntimeOptions);
}

public sealed class DocumentationScribeRuntime
{
    private readonly IDocumentationScribeModelExchange exchange;
    private readonly DocumentationScribeToolRegistry registry;
    private readonly DocumentationScribeRuntimeOptions options;
    private readonly TimeProvider timeProvider;

    public DocumentationScribeRuntime(
        IDocumentationScribeModelExchange exchange,
        DocumentationScribeToolRegistry registry,
        DocumentationScribeRuntimeOptions options)
        : this(exchange, registry, options, TimeProvider.System)
    {
    }

    internal DocumentationScribeRuntime(
        IDocumentationScribeModelExchange exchange,
        DocumentationScribeToolRegistry registry,
        DocumentationScribeRuntimeOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.exchange = exchange;
        this.registry = registry;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public async Task<DocumentationScribeRunResult> RunAsync(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribePromptInput promptInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(promptInput);
        if (string.IsNullOrEmpty(attemptId.Value))
        {
            throw new ArgumentException("A validated attempt identity is required.", nameof(attemptId));
        }

        var state = new RunState(request, attemptId, options, registry, timeProvider);
        var reducer = new DocumentationScribeTerminalReducer();
        var initial = CommitCheckpoint(state, reducer, cancellationToken);
        if (initial is not null)
        {
            return initial;
        }

        if (!string.Equals(registry.ToolPolicyId, request.ToolPolicyId, StringComparison.Ordinal)
            || !DocumentationScribePromptBuilder.IsPromptInputValid(request, promptInput))
        {
            return reducer.CommitValidation(state, cancellationToken, "scribe.prompt.invalid");
        }

        while (true)
        {
            var checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            if (state.ProviderRequestCount >= request.Limits.MaximumProviderRequests)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            if (state.ProviderRequestCount > 0 && !state.CanStartAdditionalModelWork)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            DocumentationScribeModelRequest modelRequest;
            try
            {
                modelRequest = DocumentationScribePromptBuilder.BuildRequest(
                    request,
                    attemptId,
                    promptInput,
                    registry,
                    state.AttemptNumber,
                    state.ProviderRequestCount + 1,
                    state.ToolCallCount,
                    state.RemainingOutputTokens,
                    state.CompletedToolExchanges);
            }
            catch (PromptBoundaryException)
            {
                return reducer.CommitValidation(state, cancellationToken, "scribe.prompt.over-budget");
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return reducer.CommitInternal(state, cancellationToken);
            }

            state.ProviderRequestCount++;
            OperationCompletion<DocumentationScribeModelResponse> completion;
            try
            {
                completion = await SendAsync(
                    modelRequest,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return reducer.CommitInternal(state, cancellationToken);
            }

            if (completion.Kind == OperationCompletionKind.Cancelled)
            {
                return reducer.CommitCancelled(state, cancellationToken);
            }

            if (completion.Kind == OperationCompletionKind.TimedOut)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Timeout);
            }

            if (completion.Kind == OperationCompletionKind.Faulted || completion.Value is null)
            {
                return reducer.CommitInternal(state, cancellationToken);
            }

            var response = completion.Value;
            var observationProtocolFailure = !state.TryApplyObservations(response);
            checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            if (observationProtocolFailure)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, "provider-response.invalid");
            }

            var hasCalls = response.ToolCalls.Length > 0;
            var hasTerminals = response.TerminalSubmissions.Length > 0;
            var hasFailure = response.Failure is not null;
            if (Convert.ToInt32(hasCalls) + Convert.ToInt32(hasTerminals) + Convert.ToInt32(hasFailure) != 1
                || hasTerminals && response.TerminalSubmissions.Length != 1)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, "provider-response.conflict");
            }

            if (hasFailure)
            {
                var failure = response.Failure!;
                if (!failure.IsTransient || state.AttemptNumber >= request.Limits.MaximumAttempts)
                {
                    return reducer.CommitProvider(state, cancellationToken);
                }

                if (state.ProviderRequestCount >= request.Limits.MaximumProviderRequests
                    || !state.CanStartAdditionalModelWork)
                {
                    return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
                }

                if (failure.RetryAfterMilliseconds is { } retryAfter)
                {
                    var delay = await DelayAsync(
                        retryAfter,
                        state,
                        cancellationToken).ConfigureAwait(false);
                    if (delay == OperationCompletionKind.Cancelled)
                    {
                        return reducer.CommitCancelled(state, cancellationToken);
                    }

                    if (delay == OperationCompletionKind.TimedOut)
                    {
                        return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Timeout);
                    }
                }

                state.AttemptNumber++;
                state.CompletedToolExchanges = [];
                continue;
            }

            if (hasCalls)
            {
                var roundResult = await ProcessToolRoundAsync(
                    response.ToolCalls,
                    state,
                    reducer,
                    cancellationToken).ConfigureAwait(false);
                if (roundResult is not null)
                {
                    return roundResult;
                }

                continue;
            }

            checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            return reducer.CommitTerminal(
                state,
                cancellationToken,
                response.TerminalSubmissions[0].TerminalUtf8Json);
        }
    }

    private async Task<DocumentationScribeRunResult?> ProcessToolRoundAsync(
        ImmutableArray<DocumentationScribeModelToolCall> calls,
        RunState state,
        DocumentationScribeTerminalReducer reducer,
        CancellationToken cancellationToken)
    {
        if (calls.Length > state.Request.Limits.MaximumToolCalls - state.ToolCallCount
            || state.ToolRoundCount >= state.Request.Limits.MaximumToolRounds)
        {
            return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var roundCounts = new int[registry.Registrations.Length];
        var prepared = ImmutableArray.CreateBuilder<PreparedToolCall>(calls.Length);
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            if (call.ResponseIndex != index || !seenIds.Add(call.CallId))
            {
                return reducer.CommitToolProtocol(state, cancellationToken, "tool-call.rejected");
            }

            var registrationIndex = registry.FindRegistrationIndex(call.OperationId);
            if (registrationIndex < 0)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, "tool-call.rejected");
            }

            roundCounts[registrationIndex]++;
            if (state.PerOperationToolCalls[registrationIndex] + roundCounts[registrationIndex]
                > registry.Registrations[registrationIndex].MaximumCallsPerRun)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            try
            {
                prepared.Add(registry.Registrations[registrationIndex].Prepare(call));
            }
            catch (ToolProtocolException exception)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, exception.ReferenceId);
            }
            catch (ToolBoundaryInternalException)
            {
                return reducer.CommitInternal(state, cancellationToken);
            }
        }

        state.ToolRoundCount++;
        var buffered = ImmutableArray.CreateBuilder<DocumentationScribeCompletedToolExchange>(prepared.Count);
        long prospectiveEvidenceItems = state.EvidenceItemCount;
        long prospectiveEvidenceBytes = state.EvidenceUtf8ByteCount;
        foreach (var toolCall in prepared)
        {
            var checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            var registrationIndex = registry.FindRegistrationIndex(toolCall.Call.OperationId);
            state.ToolCallCount++;
            state.PerOperationToolCalls[registrationIndex]++;
            OperationCompletion<ToolInvocationResult> completion;
            try
            {
                completion = await InvokeToolAsync(toolCall, state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return reducer.CommitInternal(state, cancellationToken);
            }

            if (completion.Kind == OperationCompletionKind.Cancelled)
            {
                return reducer.CommitCancelled(state, cancellationToken);
            }

            if (completion.Kind == OperationCompletionKind.TimedOut)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Timeout);
            }

            if (completion.Kind == OperationCompletionKind.Faulted)
            {
                if (completion.Error is ToolProtocolException protocol)
                {
                    return reducer.CommitToolProtocol(state, cancellationToken, protocol.ReferenceId);
                }

                return reducer.CommitInternal(state, cancellationToken);
            }

            var invocation = completion.Value;
            checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            if (invocation.Outcome == DocumentationScribeToolOutcome.BudgetExhausted)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            if (invocation.Outcome == DocumentationScribeToolOutcome.TimedOut)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Timeout);
            }

            if (invocation.Outcome == DocumentationScribeToolOutcome.Cancelled)
            {
                return cancellationToken.IsCancellationRequested
                    ? reducer.CommitCancelled(state, cancellationToken)
                    : reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
            }

            if (invocation.Outcome == DocumentationScribeToolOutcome.Failure)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
            }

            try
            {
                var visible = CanonicalJson.Serialize(new
                {
                    toolCall.Call.CallId,
                    toolCall.Call.OperationId,
                    outcome = invocation.Outcome.Id,
                    result = CanonicalJson.AsString(invocation.ResultUtf8Json),
                });
                prospectiveEvidenceItems = checked(
                    prospectiveEvidenceItems + invocation.EvidenceItemCount);
                prospectiveEvidenceBytes = checked(prospectiveEvidenceBytes + visible.Length);
            }
            catch (OverflowException)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            if (prospectiveEvidenceItems > state.Request.Limits.MaximumEvidenceReferences
                || prospectiveEvidenceBytes > state.Request.Limits.MaximumEvidenceUtf8Bytes)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            buffered.Add(new DocumentationScribeCompletedToolExchange(
                toolCall.Call.ResponseIndex,
                toolCall.Call.CallId,
                toolCall.Call.OperationId,
                toolCall.Call.ArgumentsUtf8JsonStorage,
                invocation.Outcome.Id,
                invocation.ResultUtf8Json));
        }

        var finalCheckpoint = CommitCheckpoint(state, reducer, cancellationToken);
        if (finalCheckpoint is not null)
        {
            return finalCheckpoint;
        }

        state.EvidenceItemCount = prospectiveEvidenceItems;
        state.EvidenceUtf8ByteCount = prospectiveEvidenceBytes;
        state.CompletedToolExchanges = state.CompletedToolExchanges.AddRange(buffered);
        return null;
    }

    private async Task<OperationCompletion<DocumentationScribeModelResponse>> SendAsync(
        DocumentationScribeModelRequest request,
        RunState state,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<DocumentationScribeModelResponse> task;
        try
        {
            task = exchange.SendAsync(request, operationCancellation.Token).AsTask();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return OperationCompletion<DocumentationScribeModelResponse>.Faulted(exception);
        }

        return await AwaitOperationAsync(
            task,
            operationCancellation,
            state,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationCompletion<ToolInvocationResult>> InvokeToolAsync(
        PreparedToolCall toolCall,
        RunState state,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ToolInvocationResult> task;
        try
        {
            task = toolCall.InvokeAsync(operationCancellation.Token).AsTask();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return OperationCompletion<ToolInvocationResult>.Faulted(exception);
        }

        return await AwaitOperationAsync(
            task,
            operationCancellation,
            state,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationCompletion<T>> AwaitOperationAsync<T>(
        Task<T> task,
        CancellationTokenSource operationCancellation,
        RunState state,
        CancellationToken cancellationToken)
    {
        var remaining = state.RemainingMilliseconds;
        if (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            ObserveLate(task);
            return OperationCompletion<T>.Cancelled();
        }

        if (remaining <= 0)
        {
            operationCancellation.Cancel();
            ObserveLate(task);
            return OperationCompletion<T>.TimedOut();
        }

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(remaining), timeProvider);
        var winner = await Task.WhenAny(task, cancellationTask, timeoutTask).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            ObserveLate(task);

            return OperationCompletion<T>.Cancelled();
        }

        if (state.RemainingMilliseconds <= 0 || winner == timeoutTask)
        {
            operationCancellation.Cancel();
            ObserveLate(task);

            return OperationCompletion<T>.TimedOut();
        }

        try
        {
            return OperationCompletion<T>.Completed(await task.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationCompletion<T>.Cancelled();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return OperationCompletion<T>.Faulted(exception);
        }
    }

    private async Task<OperationCompletionKind> DelayAsync(
        int milliseconds,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (milliseconds == 0)
        {
            return OperationCompletionKind.Completed;
        }

        if (milliseconds >= state.RemainingMilliseconds)
        {
            return OperationCompletionKind.TimedOut;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationCompletionKind.Cancelled;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return OperationCompletionKind.Cancelled;
        }

        return state.RemainingMilliseconds <= 0
            ? OperationCompletionKind.TimedOut
            : OperationCompletionKind.Completed;
    }

    private static DocumentationScribeRunResult? CommitCheckpoint(
        RunState state,
        DocumentationScribeTerminalReducer reducer,
        CancellationToken cancellationToken) =>
        reducer.TryCommitPriority(state, cancellationToken);

    private static void ObserveLate(Task task) => _ = ObserveLateAsync(task);

    internal static async Task ObserveLateAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
        }
    }

    public override string ToString() => nameof(DocumentationScribeRuntime);
}

internal sealed class DocumentationScribeTerminalReducer
{
    private readonly object gate = new();
    private DocumentationScribeRunResult? committed;

    internal DocumentationScribeRunResult? TryCommitPriority(
        RunState state,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed);
            return committed;
        }
    }

    internal DocumentationScribeRunResult CommitCancelled(
        RunState state,
        CancellationToken cancellationToken) =>
        CommitFailureCore(state, cancellationToken, DocumentationScribeFailureCode.Internal, cancelled: true);

    internal DocumentationScribeRunResult CommitFailure(
        RunState state,
        CancellationToken cancellationToken,
        DocumentationScribeFailureCode code) =>
        CommitFailureCore(state, cancellationToken, code);

    internal DocumentationScribeRunResult CommitProvider(
        RunState state,
        CancellationToken cancellationToken) =>
        CommitFailureCore(state, cancellationToken, DocumentationScribeFailureCode.Provider);

    internal DocumentationScribeRunResult CommitToolProtocol(
        RunState state,
        CancellationToken cancellationToken,
        string referenceId) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.ToolProtocol,
            detail: referenceId);

    internal DocumentationScribeRunResult CommitValidation(
        RunState state,
        CancellationToken cancellationToken,
        string validationCode) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.Validation,
            detail: validationCode);

    internal DocumentationScribeRunResult CommitInternal(
        RunState state,
        CancellationToken cancellationToken) =>
        CommitFailureCore(state, cancellationToken, DocumentationScribeFailureCode.Internal);

    internal DocumentationScribeRunResult CommitTerminal(
        RunState state,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> terminalUtf8Json)
    {
        DocumentationScribeResultParseResult? candidate = null;
        string? validationCode = null;
        try
        {
            var validationElapsed = state.ElapsedMilliseconds;
            var validationEnvelope = state.CreateEnvelope(
                ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                validationElapsed);
            var validationBytes = DocumentationScribeRunResultWriter.Write(
                state.Request,
                state.AttemptId,
                terminalUtf8Json,
                validationEnvelope);
            candidate = DocumentationScribeValidation.ParseRunResult(
                state.Request,
                state.AttemptId,
                validationBytes.AsMemory());
            if (candidate.Result is null
                || candidate.Result.Terminal.Kind is not (DocumentationScribeTerminalKind.Proposal
                    or DocumentationScribeTerminalKind.Skip))
            {
                validationCode = candidate.Failure?.Code ?? "scribe.result.invalid-terminal";
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            validationCode = "scribe.result.invalid-terminal";
        }

        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed);
            if (committed is not null)
            {
                return committed;
            }

            committed = validationCode is null && candidate?.Result is { } validated
                ? DocumentationScribeValidation.CreateResultFromValidatedTerminal(
                    state.Request,
                    state.AttemptId,
                    validated,
                    state.CreateEnvelope(
                        ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                        elapsed))
                : state.CreateValidationFailure(
                    validationCode ?? "scribe.result.invalid-terminal",
                    elapsed);

            return committed;
        }
    }

    private DocumentationScribeRunResult CommitFailureCore(
        RunState state,
        CancellationToken cancellationToken,
        DocumentationScribeFailureCode code,
        string? detail = null,
        bool cancelled = false)
    {
        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed);
            if (committed is not null)
            {
                return committed;
            }

            committed = cancelled
                ? state.CreateCancelled(elapsed)
                : code switch
                {
                    DocumentationScribeFailureCode.Provider => state.CreateProviderFailure(elapsed),
                    DocumentationScribeFailureCode.ToolProtocol =>
                        state.CreateToolProtocolFailure(detail ?? "tool", elapsed),
                    DocumentationScribeFailureCode.Validation =>
                        state.CreateValidationFailure(detail ?? "scribe.result.invalid-terminal", elapsed),
                    DocumentationScribeFailureCode.Internal => state.CreateInternalFailure(elapsed),
                    _ => state.CreateFailure(code, elapsed),
                };
            return committed;
        }
    }

    private static DocumentationScribeRunResult? CreatePriorityResult(
        RunState state,
        CancellationToken cancellationToken,
        int elapsedMilliseconds)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return state.CreateCancelled(elapsedMilliseconds);
        }

        if (elapsedMilliseconds >= state.Request.Limits.MaximumElapsedMilliseconds)
        {
            return state.CreateFailure(DocumentationScribeFailureCode.Timeout, elapsedMilliseconds);
        }

        return state.IsObservedBudgetExceeded
            ? state.CreateFailure(DocumentationScribeFailureCode.Budget, elapsedMilliseconds)
            : null;
    }
}

internal sealed class RunState
{
    private readonly DocumentationScribeRuntimeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly long startedAt;
    private long? inputTokens;
    private long? outputTokens;
    private long? cachedInputTokens;
    private long? uncachedInputTokens;
    private long? reasoningTokens;
    private long? costMicrounits;
    private string? currencyId;
    private DocumentationScribeCacheObservation? cache;
    private bool arithmeticOverflow;

    internal RunState(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRuntimeOptions options,
        DocumentationScribeToolRegistry registry,
        TimeProvider timeProvider)
    {
        Request = request;
        AttemptId = attemptId;
        this.options = options;
        this.timeProvider = timeProvider;
        startedAt = timeProvider.GetTimestamp();
        AttemptNumber = 1;
        PerOperationToolCalls = new int[registry.Registrations.Length];
        EvidenceItemCount = request.EvidenceReferences.Length;
        try
        {
            EvidenceUtf8ByteCount = request.EvidenceReferences.Aggregate(
                0L,
                (total, reference) => checked(total + reference.IncludedUtf8ByteCount));
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
        }
    }

    internal DocumentationScribeRequest Request { get; }

    internal DocumentationScribeAttemptId AttemptId { get; }

    internal int AttemptNumber { get; set; }

    internal int ProviderRequestCount { get; set; }

    internal int ToolRoundCount { get; set; }

    internal int ToolCallCount { get; set; }

    internal int[] PerOperationToolCalls { get; }

    internal long EvidenceItemCount { get; set; }

    internal long EvidenceUtf8ByteCount { get; set; }

    internal ImmutableArray<DocumentationScribeCompletedToolExchange> CompletedToolExchanges { get; set; } = [];

    internal int ElapsedMilliseconds
    {
        get
        {
            var elapsed = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            return (int)Math.Clamp(
                Math.Ceiling(elapsed),
                0,
                DocumentationScribeContract.MaximumObservedElapsedMilliseconds);
        }
    }

    internal int RemainingMilliseconds => Math.Max(
        0,
        Request.Limits.MaximumElapsedMilliseconds - ElapsedMilliseconds);

    internal bool IsObservedBudgetExceeded => arithmeticOverflow
        || HasUnrepresentableObservation
        || EvidenceItemCount > Request.Limits.MaximumEvidenceReferences
        || EvidenceUtf8ByteCount > Request.Limits.MaximumEvidenceUtf8Bytes
        || inputTokens > Request.Limits.MaximumInputTokens
        || outputTokens > Request.Limits.MaximumOutputTokens
        || cachedInputTokens > Request.Limits.MaximumInputTokens
        || uncachedInputTokens > Request.Limits.MaximumUncachedInputTokens
        || reasoningTokens > Request.Limits.MaximumOutputTokens
        || costMicrounits > Request.Limits.MaximumCostMicrounits;

    internal bool CanStartAdditionalModelWork => !arithmeticOverflow
        && !HasUnrepresentableObservation
        && (inputTokens is null || inputTokens < Request.Limits.MaximumInputTokens)
        && (outputTokens is null || outputTokens < Request.Limits.MaximumOutputTokens)
        && (cachedInputTokens is null || cachedInputTokens < Request.Limits.MaximumInputTokens)
        && (uncachedInputTokens is null || uncachedInputTokens < Request.Limits.MaximumUncachedInputTokens)
        && (reasoningTokens is null || reasoningTokens < Request.Limits.MaximumOutputTokens)
        && (costMicrounits is null || costMicrounits < Request.Limits.MaximumCostMicrounits);

    internal int RemainingOutputTokens => outputTokens is null
        ? Request.Limits.MaximumOutputTokens
        : (int)Math.Max(0, Request.Limits.MaximumOutputTokens - outputTokens.Value);

    private bool HasUnrepresentableObservation => inputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || outputTokens > DocumentationScribeContract.MaximumObservedOutputTokens
        || cachedInputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || uncachedInputTokens > DocumentationScribeContract.MaximumObservedInputTokens
        || reasoningTokens > DocumentationScribeContract.MaximumObservedOutputTokens
        || costMicrounits > DocumentationScribeContract.MaximumObservedCostMicrounits;

    internal bool TryApplyObservations(DocumentationScribeModelResponse response)
    {
        try
        {
            if (response.Usage is { } usage)
            {
                Add(ref inputTokens, usage.InputTokens);
                Add(ref outputTokens, usage.OutputTokens);
                Add(ref cachedInputTokens, usage.CachedInputTokens);
                Add(ref uncachedInputTokens, usage.UncachedInputTokens);
                Add(ref reasoningTokens, usage.ReasoningTokens);
            }

            if (response.Cost is { } cost)
            {
                if (currencyId is not null
                    && !string.Equals(currencyId, cost.CurrencyId, StringComparison.Ordinal))
                {
                    return false;
                }

                currencyId ??= cost.CurrencyId;
                Add(ref costMicrounits, cost.AmountMicrounits);
            }

            if (response.Cache is { } reported)
            {
                cache = cache is null || cache == reported
                    ? reported
                    : DocumentationScribeCacheObservation.Mixed;
            }

            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return true;
        }
    }

    internal DocumentationScribeRunResult CreateCancelled(int elapsedMilliseconds) => DocumentationScribeValidation.CreateCancelledResult(
        Request,
        AttemptId,
        DocumentationScribeCancellationCode.Caller,
        CreateEnvelope(
            ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
            elapsedMilliseconds,
            allowObservedOverrun: true));

    internal DocumentationScribeRunResult CreateFailure(
        DocumentationScribeFailureCode code,
        int elapsedMilliseconds) =>
        DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            code,
            CreateEnvelope(
                ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                elapsedMilliseconds,
                allowObservedOverrun: code is DocumentationScribeFailureCode.Budget
                    or DocumentationScribeFailureCode.Timeout));

    internal DocumentationScribeRunResult CreateProviderFailure(int elapsedMilliseconds) => DocumentationScribeValidation.CreateFailureResult(
        Request,
        AttemptId,
        DocumentationScribeFailureCode.Provider,
        CreateEnvelope(
            [new DocumentationScribeDiagnosticInput("scribe.diagnostic.provider-failure", "provider")],
            elapsedMilliseconds,
            allowObservedOverrun: false));

    internal DocumentationScribeRunResult CreateToolProtocolFailure(
        string referenceId,
        int elapsedMilliseconds) =>
        DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            DocumentationScribeFailureCode.ToolProtocol,
            CreateEnvelope(
                [new DocumentationScribeDiagnosticInput(
                    "scribe.diagnostic.tool-failure",
                    "tool",
                    DocumentationScribeBoundary.ValidateIdentifier(referenceId, nameof(referenceId)))],
                elapsedMilliseconds,
                allowObservedOverrun: false));

    internal DocumentationScribeRunResult CreateValidationFailure(
        string validationCode,
        int elapsedMilliseconds) =>
        DocumentationScribeValidation.CreateFailureResult(
            Request,
            AttemptId,
            DocumentationScribeFailureCode.Validation,
            CreateEnvelope(
                [new DocumentationScribeDiagnosticInput(
                    "scribe.diagnostic.result-rejected",
                    "result",
                    ValidationCode: DocumentationScribeBoundary.ValidateIdentifier(
                        validationCode,
                        nameof(validationCode)))],
                elapsedMilliseconds,
                allowObservedOverrun: false));

    internal DocumentationScribeRunResult CreateInternalFailure(int elapsedMilliseconds) => DocumentationScribeValidation.CreateFailureResult(
        Request,
        AttemptId,
        DocumentationScribeFailureCode.Internal,
        CreateEnvelope(
            [new DocumentationScribeDiagnosticInput("scribe.diagnostic.runtime-failure", "runtime")],
            elapsedMilliseconds,
            allowObservedOverrun: false));

    internal DocumentationScribeRunEnvelopeInput CreateEnvelope(
        ImmutableArray<DocumentationScribeDiagnosticInput> diagnostics,
        int elapsedMilliseconds,
        bool allowObservedOverrun = false)
    {
        var usage = CreateUsage(allowObservedOverrun);
        var cost = CreateCost(allowObservedOverrun);
        return new DocumentationScribeRunEnvelopeInput(
            options.ProviderConfigurationId,
            options.ModelConfigurationId,
            options.ScribeProtocolId,
            AttemptNumber,
            ProviderRequestCount,
            ToolRoundCount,
            ToolCallCount,
            elapsedMilliseconds,
            usage,
            cache,
            cost,
            diagnostics);
    }

    private DocumentationScribeUsageObservationInput? CreateUsage(bool allowObservedOverrun)
    {
        if (inputTokens is null
            && outputTokens is null
            && cachedInputTokens is null
            && uncachedInputTokens is null
            && reasoningTokens is null)
        {
            return null;
        }

        if (!allowObservedOverrun && (inputTokens > Request.Limits.MaximumInputTokens
            || outputTokens > Request.Limits.MaximumOutputTokens
            || cachedInputTokens > Request.Limits.MaximumInputTokens
            || uncachedInputTokens > Request.Limits.MaximumUncachedInputTokens
            || reasoningTokens > Request.Limits.MaximumOutputTokens))
        {
            return null;
        }

        var representableInput = Representable(inputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableOutput = Representable(outputTokens, DocumentationScribeContract.MaximumObservedOutputTokens);
        var representableCached = Representable(cachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableUncached = Representable(uncachedInputTokens, DocumentationScribeContract.MaximumObservedInputTokens);
        var representableReasoning = Representable(reasoningTokens, DocumentationScribeContract.MaximumObservedOutputTokens);
        return representableInput is null
            && representableOutput is null
            && representableCached is null
            && representableUncached is null
            && representableReasoning is null
                ? null
                : new DocumentationScribeUsageObservationInput(
                    representableInput,
                    representableOutput,
                    representableCached,
                    representableUncached,
                    representableReasoning);
    }

    private DocumentationScribeCostObservationInput? CreateCost(bool allowObservedOverrun)
    {
        if (currencyId is null
            || costMicrounits is null
            || costMicrounits > DocumentationScribeContract.MaximumObservedCostMicrounits)
        {
            return null;
        }

        if (!allowObservedOverrun && costMicrounits > Request.Limits.MaximumCostMicrounits)
        {
            return null;
        }

        return new DocumentationScribeCostObservationInput(
            currencyId,
            costMicrounits.Value);
    }

    private static void Add(ref long? total, int? delta)
    {
        if (delta is { } value)
        {
            total = checked((total ?? 0) + value);
        }
    }

    private static void Add(ref long? total, long delta) => total = checked((total ?? 0) + delta);

    private static int? Representable(long? value, int maximum) => value is null || value > maximum
        ? null
        : (int)value.Value;
}

internal enum OperationCompletionKind
{
    Completed,
    Cancelled,
    TimedOut,
    Faulted,
}

internal readonly struct OperationCompletion<T>
{
    private OperationCompletion(OperationCompletionKind kind, T? value, Exception? error)
    {
        Kind = kind;
        Value = value;
        Error = error;
    }

    internal OperationCompletionKind Kind { get; }

    internal T? Value { get; }

    internal Exception? Error { get; }

    internal static OperationCompletion<T> Completed(T value) => new(OperationCompletionKind.Completed, value, null);

    internal static OperationCompletion<T> Cancelled() => new(OperationCompletionKind.Cancelled, default, null);

    internal static OperationCompletion<T> TimedOut() => new(OperationCompletionKind.TimedOut, default, null);

    internal static OperationCompletion<T> Faulted(Exception error) => new(OperationCompletionKind.Faulted, default, error);
}
