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

    public DocumentationScribeRuntime(
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
        CancellationToken cancellationToken = default,
        DocumentationScribeExecutionScope? executionScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(promptInput);
        if (string.IsNullOrEmpty(attemptId.Value))
        {
            throw new ArgumentException("A validated attempt identity is required.", nameof(attemptId));
        }

        var scope = executionScope ?? DocumentationScribeExecutionScope.ForStandalone(request, timeProvider);
        var state = new RunState(request, attemptId, options, registry, scope.Allowance.Clock, scope);
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

        if (scope.PendingRetryAfterMilliseconds > 0)
        {
            var restoredDelay = await DelayAsync(scope.PendingRetryAfterMilliseconds, state, cancellationToken).ConfigureAwait(false);
            if (restoredDelay != OperationCompletionKind.Completed)
                return restoredDelay == OperationCompletionKind.Cancelled ? reducer.CommitCancelled(state, cancellationToken)
                    : reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
        }
        while (true)
        {
            var checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            if (!scope.Allowance.CanBeginProviderWork()
                || state.ProviderRequestCount >= request.Limits.MaximumProviderRequests)
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
                    Math.Min(state.RemainingOutputTokens, scope.Allowance.Limits.MaximumRequestOutputTokens),
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

            state.ProviderRequestCount = state.Scope.PhysicalProviderRequestCount;
            if (completion.Kind == OperationCompletionKind.Cancelled)
            {
                return reducer.CommitCancelled(state, cancellationToken);
            }

            if (completion.Kind == OperationCompletionKind.TimedOut)
            {
                return reducer.CommitFailure(state, cancellationToken,
                    state.Scope.Allowance.HasCheckedStop || state.Scope.LifetimeDeadlineReached
                        ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
            }

            if (completion.Kind == OperationCompletionKind.Faulted || completion.Value is null)
            {
                return reducer.CommitInternal(state, cancellationToken);
            }

            var response = completion.Value;
            var observationProtocolFailure = !state.TryApplyObservations(response);
            var returnedTerminal = !observationProtocolFailure && response.TerminalSubmissions.Length == 1
                && response.ToolCalls.Length == 0 && response.Failure is null;
            checkpoint = CommitCheckpoint(state, reducer, cancellationToken, allowReturnedTerminal: returnedTerminal);
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
                if (failure.Origin == DocumentationScribeModelFailureOrigin.RequestPreparation)
                    return reducer.CommitValidation(state, cancellationToken, "scribe.provider.not-sent");
                if (!failure.IsTransient || state.AttemptNumber >= request.Limits.MaximumAttempts)
                {
                    return reducer.CommitProvider(
                        state,
                        cancellationToken,
                        MapProviderFinalDisposition(failure.Code));
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
                        return reducer.CommitFailure(state, cancellationToken,
                    state.Scope.Allowance.HasCheckedStop || state.Scope.LifetimeDeadlineReached
                        ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
                    }
                }

                state.AttemptNumber++;
                state.CompletedToolExchanges = [];
                state.ActiveDynamicEvidenceReferences = [];
                continue;
            }

            if (hasCalls)
            {
                var roundResult = await ProcessToolRoundAsync(
                    response.ToolCalls,
                    response.AssistantContinuation,
                    state,
                    reducer,
                    cancellationToken).ConfigureAwait(false);
                if (roundResult is not null)
                {
                    return roundResult;
                }

                continue;
            }

            checkpoint = CommitCheckpoint(state, reducer, cancellationToken, allowReturnedTerminal: true);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            var submission = await scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, cancellationToken).ConfigureAwait(false);
            if (submission is null || !submission.TryBeginOperation())
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            try
            {
                state.ToolCallCount++;
                return reducer.CommitTerminal(state, cancellationToken, response.TerminalSubmissions[0].TerminalUtf8Json);
            }
            finally { await scope.CompleteHostAsync(submission, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    internal static DocumentationScribeProviderFinalDisposition MapProviderFinalDisposition(
        DocumentationScribeModelFailureCode failureCode) => failureCode switch
        {
            DocumentationScribeModelFailureCode.TransientUnavailable =>
                DocumentationScribeProviderFinalDisposition.Retryable,
            DocumentationScribeModelFailureCode.RateLimited =>
                DocumentationScribeProviderFinalDisposition.Retryable,
            _ => DocumentationScribeProviderFinalDisposition.Terminal,
        };

    private async Task<DocumentationScribeRunResult?> ProcessToolRoundAsync(
        ImmutableArray<DocumentationScribeModelToolCall> calls,
        DocumentationScribeAssistantContinuation? assistantContinuation,
        RunState state,
        DocumentationScribeTerminalReducer reducer,
        CancellationToken cancellationToken)
    {
        if (calls.Length > state.Scope.Allowance.Limits.MaximumToolCallsPerResponse
            || calls.Length > state.Scope.Allowance.RemainingToolCalls
            || calls.Length > state.Request.Limits.MaximumToolCalls - state.ToolCallCount
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
        var prospectiveActive = state.ActiveDynamicEvidenceReferences.ToImmutableDictionary(
            item => item.EvidenceReferenceId,
            StringComparer.Ordinal).ToBuilder();
        var requestEvidenceById = state.Request.EvidenceReferences.ToImmutableDictionary(
            item => item.EvidenceReferenceId,
            StringComparer.Ordinal);
        foreach (var toolCall in prepared)
        {
            var checkpoint = CommitCheckpoint(state, reducer, cancellationToken);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            var registrationIndex = registry.FindRegistrationIndex(toolCall.Call.OperationId);

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
                return reducer.CommitFailure(state, cancellationToken,
                    state.Scope.Allowance.HasCheckedStop || state.Scope.LifetimeDeadlineReached
                        ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
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
                return reducer.CommitFailure(state, cancellationToken,
                    state.Scope.Allowance.HasCheckedStop || state.Scope.LifetimeDeadlineReached
                        ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
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

            if (invocation.Outcome == DocumentationScribeToolOutcome.Unavailable
                && invocation.DynamicEvidence.Length > 0)
            {
                return reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
            }

            var callReferences = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceReference>();
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in invocation.DynamicEvidence)
            {
                if (!DocumentationScribeValidation.TryCreateDynamicEvidenceReference(
                        state.Request,
                        input,
                        out var created)
                    || created is null
                    || !callIds.Add(created.EvidenceReferenceId))
                {
                    return reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
                }

                DocumentationScribeEvidenceReference accepted;
                if (requestEvidenceById.TryGetValue(created.EvidenceReferenceId, out var original))
                {
                    if (!EvidenceReferenceEquivalent(original, created))
                    {
                        return reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
                    }

                    accepted = original;
                }
                else if (state.ChargedDynamicEvidenceById.TryGetValue(created.EvidenceReferenceId, out var charged))
                {
                    if (!EvidenceReferenceEquivalent(charged, created))
                    {
                        return reducer.CommitToolProtocol(state, cancellationToken, toolCall.Call.OperationId);
                    }

                    accepted = charged;
                    prospectiveActive[accepted.EvidenceReferenceId] = accepted;
                }
                else
                {
                    accepted = created;
                    prospectiveActive.Add(accepted.EvidenceReferenceId, accepted);
                    if (!state.TryChargeDynamicEvidence(accepted))
                    {
                        return reducer.CommitFailure(
                            state,
                            cancellationToken,
                            DocumentationScribeFailureCode.Budget);
                    }
                }

                callReferences.Add(accepted);
            }

            var orderedCallReferences = callReferences
                .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
                .ToImmutableArray();

            var completedExchange = new DocumentationScribeCompletedToolExchange(
                toolCall.Call.ResponseIndex,
                toolCall.Call.CallId,
                toolCall.Call.OperationId,
                toolCall.Call.ArgumentsUtf8JsonStorage,
                invocation.Outcome.Id,
                invocation.ResultUtf8Json,
                orderedCallReferences,
                toolCall.Call.ResponseIndex == 0 ? assistantContinuation : null);
            if (!state.TryChargeSuccessfulToolExchange(
                    DocumentationScribePromptBuilder.MeasureCompletedToolExchange(completedExchange)))
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            if (state.IsEvidenceBudgetExceeded)
            {
                return reducer.CommitFailure(state, cancellationToken, DocumentationScribeFailureCode.Budget);
            }

            buffered.Add(completedExchange);
        }

        var finalCheckpoint = CommitCheckpoint(state, reducer, cancellationToken);
        if (finalCheckpoint is not null)
        {
            return finalCheckpoint;
        }

        state.ActiveDynamicEvidenceReferences = prospectiveActive.Values
            .OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
        state.CompletedToolExchanges = state.CompletedToolExchanges.AddRange(buffered);
        return null;
    }

    private async Task<OperationCompletion<DocumentationScribeModelResponse>> SendAsync(
        DocumentationScribeModelRequest request, RunState state, CancellationToken cancellationToken)
    {
        try
        {
            // The physical exchange owns its deadline and awaits mandatory settlement before returning.
            var response = await exchange.SendAsync(request, state.Scope, cancellationToken).ConfigureAwait(false);
            return OperationCompletion<DocumentationScribeModelResponse>.Completed(response);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? OperationCompletion<DocumentationScribeModelResponse>.Cancelled()
            : OperationCompletion<DocumentationScribeModelResponse>.TimedOut();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return OperationCompletion<DocumentationScribeModelResponse>.Faulted(exception); }
    }

    private async Task<OperationCompletion<ToolInvocationResult>> InvokeToolAsync(
        PreparedToolCall toolCall, RunState state, CancellationToken cancellationToken)
    {
        var phase = toolCall.Call.OperationId is "read-excerpt" or "list-files" or "search-text"
            ? DocumentationScribeHostOperation.RepositoryTool
            : toolCall.Call.OperationId == "get-target-evidence"
                ? DocumentationScribeHostOperation.SemanticTool : DocumentationScribeHostOperation.RegisteredTool;
        var permit = await state.Scope.BeginHostAsync(phase, cancellationToken).ConfigureAwait(false);
        if (permit is null || !permit.TryBeginOperation()) return OperationCompletion<ToolInvocationResult>.TimedOut();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            state.ToolCallCount++;
            state.PerOperationToolCalls[registry.FindRegistrationIndex(toolCall.Call.OperationId)]++;
            var task = toolCall.InvokeAsync(operationCancellation.Token).AsTask();
            var completion = await AwaitOperationAsync(task, operationCancellation, state, cancellationToken, permit.RemainingMilliseconds).ConfigureAwait(false);
            if (completion.Kind == OperationCompletionKind.TimedOut
                || completion.Kind == OperationCompletionKind.Completed && completion.Value.Outcome == DocumentationScribeToolOutcome.TimedOut && permit.RemainingMilliseconds <= 0)
                state.Scope.ObserveDeadline(permit);
            return completion;
        }
        finally
        {
            if (!await state.Scope.CompleteHostAsync(permit, CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("scribe.tool.settlement-unconfirmed");
        }
    }

    private async Task<OperationCompletion<T>> AwaitOperationAsync<T>(
        Task<T> task,
        CancellationTokenSource operationCancellation,
        RunState state,
        CancellationToken cancellationToken, int? operationRemaining = null)
    {
        var remaining = Math.Min(state.RemainingMilliseconds, operationRemaining ?? int.MaxValue);
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

        using var boundaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var boundaryTask = Task.Delay(
            TimeSpan.FromMilliseconds(remaining),
            timeProvider,
            boundaryCancellation.Token);
        Task winner;
        try
        {
            winner = await Task.WhenAny(task, boundaryTask).ConfigureAwait(false);
        }
        finally
        {
            boundaryCancellation.Cancel();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            ObserveLate(task);

            return OperationCompletion<T>.Cancelled();
        }

        if (state.RemainingMilliseconds <= 0 || winner == boundaryTask)
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

    private async Task<OperationCompletionKind> DelayAsync(int milliseconds, RunState state, CancellationToken cancellationToken)
    {
        while (milliseconds > 0)
        {
            var pendingBefore = state.Scope.PendingRetryAfterMilliseconds;
            var permit = await state.Scope.BeginHostAsync(DocumentationScribeHostOperation.RetryWait, cancellationToken).ConfigureAwait(false);
            if (permit is null) return OperationCompletionKind.TimedOut;
            try
            {
                var begun = permit.TryBeginOperation();
                if (!begun && (permit.RemainingMilliseconds > 0 || state.Scope.Failed || state.Scope.Allowance.HasCheckedStop
                    || permit.DeadlineOwner != DocumentationScribeDeadlineOwner.HostOperation))
                {
                    state.Scope.ObserveDeadline(permit);
                    return OperationCompletionKind.TimedOut;
                }
                // The delay is itself the finite host operation. Reservation/readback
                // time already consumes its interval; a competing equal timer would
                // cancel a normally completed wait before the provider may retry.
                var remaining = permit.RemainingMilliseconds;
                if (remaining > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(remaining), state.Scope.Allowance.Clock, cancellationToken).ConfigureAwait(false);
                if (permit.DeadlineOwner != DocumentationScribeDeadlineOwner.HostOperation)
                {
                    state.Scope.ObserveDeadline(permit);
                    return OperationCompletionKind.TimedOut;
                }
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested) state.Scope.ObserveDeadline(permit);
                return cancellationToken.IsCancellationRequested ? OperationCompletionKind.Cancelled : OperationCompletionKind.TimedOut;
            }
            finally { await state.Scope.CompleteHostAsync(permit, CancellationToken.None).ConfigureAwait(false); }
            milliseconds = state.Scope.PendingRetryAfterMilliseconds;
            if (milliseconds >= pendingBefore) return OperationCompletionKind.TimedOut;
        }
        return OperationCompletionKind.Completed;
    }

    private static DocumentationScribeRunResult? CommitCheckpoint(
        RunState state,
        DocumentationScribeTerminalReducer reducer,
        CancellationToken cancellationToken, bool allowReturnedTerminal = false) =>
        reducer.TryCommitPriority(state, cancellationToken, allowReturnedTerminal);

    private static bool EvidenceReferenceEquivalent(
        DocumentationScribeEvidenceReference left,
        DocumentationScribeEvidenceReference right) =>
        string.Equals(left.EvidenceReferenceId, right.EvidenceReferenceId, StringComparison.Ordinal)
        && left.RepositoryContextRef == right.RepositoryContextRef
        && Equals(left.Subject, right.Subject)
        && left.Kind == right.Kind
        && left.Relation == right.Relation
        && left.Authority == right.Authority
        && Equals(left.Locator, right.Locator)
        && string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
        && left.OriginalUtf8ByteCount == right.OriginalUtf8ByteCount
        && left.IncludedUtf8ByteCount == right.IncludedUtf8ByteCount
        && left.IsTruncated == right.IsTruncated
        && left.ClaimCategoryIds.SequenceEqual(right.ClaimCategoryIds, StringComparer.Ordinal);

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
        CancellationToken cancellationToken, bool allowReturnedTerminal = false)
    {
        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed, allowReturnedTerminal);
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
        CancellationToken cancellationToken,
        DocumentationScribeProviderFinalDisposition providerFinalDisposition) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.Provider,
            providerFinalDisposition: providerFinalDisposition);

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
                state.ActiveDynamicEvidenceReferences,
                terminalUtf8Json,
                validationEnvelope);
            candidate = DocumentationScribeValidation.ParseRunResult(
                state.Request,
                state.AttemptId,
                state.ActiveDynamicEvidenceReferences,
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
            committed = CreatePriorityResult(state, cancellationToken, elapsed,
                allowReturnedTerminal: validationCode is null && candidate?.Result?.Terminal is DocumentationScribeProposalTerminal);
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
        bool cancelled = false,
        DocumentationScribeProviderFinalDisposition? providerFinalDisposition = null)
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
                    DocumentationScribeFailureCode.Provider =>
                        state.CreateProviderFailure(
                            providerFinalDisposition
                            ?? DocumentationScribeProviderFinalDisposition.Terminal,
                            elapsed),
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
        int elapsedMilliseconds, bool allowReturnedTerminal = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return state.CreateCancelled(elapsedMilliseconds);
        }

        if (state.Scope.ResourceBudgetReached
            && !(allowReturnedTerminal && state.Scope.SettledLifetimeBudgetExceeded
                && !state.Scope.LifetimeDeadlineReached && !state.Scope.Allowance.HasCheckedStop))
        {
            return state.CreateFailure(DocumentationScribeFailureCode.Budget, elapsedMilliseconds);
        }

        if (elapsedMilliseconds >= state.Request.Limits.MaximumElapsedMilliseconds || state.Scope.StandaloneDeadlineReached)
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
        TimeProvider timeProvider, DocumentationScribeExecutionScope? scope = null)
    {
        Scope = scope ?? DocumentationScribeExecutionScope.ForStandalone(request, timeProvider);
        Request = request;
        AttemptId = attemptId;
        this.options = options;
        this.timeProvider = timeProvider;
        startedAt = timeProvider.GetTimestamp();
        AttemptNumber = checked(Scope.RestoredRetryableProviderFailures + 1);
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

    internal DocumentationScribeExecutionScope Scope { get; }

    internal DocumentationScribeRequest Request { get; }

    internal DocumentationScribeAttemptId AttemptId { get; }

    internal int AttemptNumber { get; set; }

    internal int ProviderRequestCount { get; set; }

    internal int ToolRoundCount { get; set; }

    internal int ToolCallCount { get; set; }

    internal int[] PerOperationToolCalls { get; }

    internal long EvidenceItemCount { get; set; }

    internal long EvidenceUtf8ByteCount { get; set; }

    internal long SuccessfulToolExchangeUtf8ByteCount { get; set; }

    internal ImmutableArray<DocumentationScribeEvidenceReference> ActiveDynamicEvidenceReferences { get; set; } = [];

    internal ImmutableDictionary<string, DocumentationScribeEvidenceReference> ChargedDynamicEvidenceById { get; set; } =
        ImmutableDictionary.Create<string, DocumentationScribeEvidenceReference>(StringComparer.Ordinal);

    internal ImmutableArray<DocumentationScribeCompletedToolExchange> CompletedToolExchanges { get; set; } = [];

    internal bool TryChargeDynamicEvidence(DocumentationScribeEvidenceReference reference)
    {
        try
        {
            var itemCount = checked(EvidenceItemCount + 1);
            var byteCount = checked(EvidenceUtf8ByteCount + reference.IncludedUtf8ByteCount);
            ChargedDynamicEvidenceById = ChargedDynamicEvidenceById.Add(
                reference.EvidenceReferenceId,
                reference);
            EvidenceItemCount = itemCount;
            EvidenceUtf8ByteCount = byteCount;
            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return false;
        }
    }

    internal bool TryChargeSuccessfulToolExchange(int utf8ByteCount)
    {
        try
        {
            SuccessfulToolExchangeUtf8ByteCount = checked(
                SuccessfulToolExchangeUtf8ByteCount + utf8ByteCount);
            return true;
        }
        catch (OverflowException)
        {
            arithmeticOverflow = true;
            return false;
        }
    }

    internal bool IsEvidenceBudgetExceeded => arithmeticOverflow
        || EvidenceItemCount > Request.Limits.MaximumEvidenceReferences
        || EvidenceUtf8ByteCount > Request.Limits.MaximumEvidenceUtf8Bytes
        || SuccessfulToolExchangeUtf8ByteCount > 33_554_432;

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
        Math.Min(Request.Limits.MaximumElapsedMilliseconds - ElapsedMilliseconds, Scope.Allowance.RemainingMilliseconds));

    internal bool IsObservedBudgetExceeded => Scope.Allowance.HasCheckedStop || Scope.LifetimeDeadlineReached || arithmeticOverflow
        || HasUnrepresentableObservation
        || EvidenceItemCount > Request.Limits.MaximumEvidenceReferences
        || EvidenceUtf8ByteCount > Request.Limits.MaximumEvidenceUtf8Bytes
        || SuccessfulToolExchangeUtf8ByteCount > 33_554_432
        || inputTokens > Request.Limits.MaximumInputTokens
        || outputTokens > Request.Limits.MaximumOutputTokens
        || cachedInputTokens > Request.Limits.MaximumInputTokens
        || uncachedInputTokens > Request.Limits.MaximumUncachedInputTokens
        || reasoningTokens > Request.Limits.MaximumOutputTokens
        || costMicrounits > Request.Limits.MaximumCostMicrounits;

    internal bool CanStartAdditionalModelWork => Scope.Allowance.CanBeginProviderWork() && !arithmeticOverflow
        && !HasUnrepresentableObservation
        && (inputTokens is null || inputTokens < Request.Limits.MaximumInputTokens)
        && (outputTokens is null || outputTokens < Request.Limits.MaximumOutputTokens)
        && (cachedInputTokens is null || cachedInputTokens < Request.Limits.MaximumInputTokens)
        && (uncachedInputTokens is null || uncachedInputTokens < Request.Limits.MaximumUncachedInputTokens)
        && (reasoningTokens is null || reasoningTokens < Request.Limits.MaximumOutputTokens)
        && (costMicrounits is null || costMicrounits < Request.Limits.MaximumCostMicrounits);

    internal int RemainingOutputTokens => Math.Min(Scope.Allowance.RemainingOutputTokens, outputTokens is null
        ? Request.Limits.MaximumOutputTokens
        : (int)Math.Max(0, Request.Limits.MaximumOutputTokens - outputTokens.Value));

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

    internal DocumentationScribeRunResult CreateProviderFailure(
        DocumentationScribeProviderFinalDisposition providerFinalDisposition,
        int elapsedMilliseconds) => DocumentationScribeValidation.CreateFailureResult(
        Request,
        AttemptId,
        DocumentationScribeFailureCode.Provider,
        CreateEnvelope(
            [new DocumentationScribeDiagnosticInput("scribe.diagnostic.provider-failure", "provider")],
            elapsedMilliseconds,
            allowObservedOverrun: false),
        providerFinalDisposition);

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
            diagnostics)
        { RestoredRetryableProviderFailures = Scope.RestoredRetryableProviderFailures };
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
