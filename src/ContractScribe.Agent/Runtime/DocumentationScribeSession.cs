using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Prompting;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace ContractScribe.Agent.Runtime;

// The Scribe-specific adapter. SolusAgent owns generic execution; product scopes own
// physical operations, read authority, accounting and independent terminal validation.
internal sealed partial class DocumentationScribeSession : ICandidateHost, IRuntimeExposureHooks
{
    private const string Instructions = "Execute the Host-bound Documentation Scribe protocol using only registered read tools.";
    private const string EnvelopeSchema = "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"payload\"],\"properties\":{\"payload\":{\"type\":\"string\"}}}";
    private readonly DocumentationScribeToolRegistry registry;
    private readonly DocumentationScribePromptInput prompt;
    private readonly ScribeProvider provider;
    private ImmutableDictionary<string, (int Epoch, DocumentationScribeCompletedToolExchange Exchange)> results =
        ImmutableDictionary.Create<string, (int, DocumentationScribeCompletedToolExchange)>(StringComparer.Ordinal);
    private ImmutableDictionary<string, DocumentationScribeModelToolCall> calls =
        ImmutableDictionary.Create<string, DocumentationScribeModelToolCall>(StringComparer.Ordinal);
    private DocumentationScribeAssistantContinuation? continuation;
    private ReadOnlyMemory<byte>? terminalBytes;
    private int epoch;
    private int observedRetryFailures;
    private bool retryAdvancePending;
    private bool retryableFailure;
    private DocumentationScribeProviderFinalDisposition providerDisposition = DocumentationScribeProviderFinalDisposition.Terminal;
    private Task hostPending = Task.CompletedTask;
    private ToolBatch? batch;
    internal readonly RunState State;
    internal CandidateExecutionResult? RuntimeResult { get; private set; }
    internal DocumentationScribeRunResult? Result { get; private set; }
    internal bool ProviderFault { get; private set; }
    internal Task ProviderCompletion => provider.Pending;
    internal Task HostCompletion => hostPending;
    internal Task<CandidateExecutionResult>? RuntimeTask { get; private set; }
    internal RuntimeConfiguration? Configuration { get; }

    internal DocumentationScribeSession(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, DocumentationScribeToolRegistry registry,
        IDocumentationScribeModelExchange exchange, DocumentationScribeRuntimeOptions options,
        TimeProvider clock, CancellationToken token, DocumentationScribeExecutionScope? executionScope = null,
        IDocumentationScribeSubmissionHost? submissionHost = null, DocumentationScribeRunResult? restoredProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (!request.HasExecutableTargets)
            throw new ArgumentException("The manifest has no executable target.", nameof(request));
        if (string.IsNullOrEmpty(attempt.Value))
            throw new ArgumentException("A validated attempt identity is required.", nameof(attempt));
        this.registry = registry;
        this.prompt = prompt;
        var scope = executionScope ?? DocumentationScribeExecutionScope.ForStandalone(request, clock);
        State = new(request, attempt, options, registry, scope.Allowance.Clock, scope);
        this.submissionHost = submissionHost;
        RestoreProgress(restoredProgress, options);
        provider = new(this, exchange, options);
        Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
        if (Result is not null) return;
        if (registry.ToolPolicyId != request.ToolPolicyId || !DocumentationScribePromptBuilder.IsPromptInputValid(request, prompt))
        {
            Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.prompt.invalid");
            return;
        }
        if (restoredProgress is not null && restoredProgress.Submissions.Any(item => item.Stage == DocumentationScribeAcknowledgementStage.Received))
        {
            Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token);
            return;
        }
        if (restoredProgress is not null && request.ExecutableTargetIds.All(State.RestoredClosedTargets.Contains))
        {
            Result = State.CreateFinished();
            return;
        }
        var tools = registry.Registrations.Select((registration, index) =>
            new RuntimeToolRegistration(new ReadTool(this, registration, RuntimeToolName(index)), new ReadCapability())).ToArray();
        Configuration = new(provider, tools, this, Bounds());
    }

    internal static ProviderExchangeBounds Bounds() => new(maximumInputs: 2048,
        maximumRequestBytes: ProviderLimits.MaximumRequestBytes, maximumResponseBytes: ProviderLimits.MaximumResponseBytes,
        maximumContinuationBytes: ProviderLimits.MaximumContinuationBytes, maximumTextBytes: ProviderLimits.MaximumTextBytes);

    internal async Task<DocumentationScribeRunResult> RunAsync(ICandidateAgent runtime, CancellationToken token = default)
    {
        State.CallerCancellation = token;
        var scope = State.Scope;
        Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
        if (Result is null && (scope.Allowance.Limits.MaximumTargets == 0 || !scope.Allowance.CanBeginProviderWork()))
            Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        if (Result is not null)
        {
            return Result;
        }
        var maximum = Math.Min(128, Math.Min(State.Request.Limits.MaximumProviderRequests, scope.Allowance.Limits.MaximumProviderRequests));
        var targetRemaining = State.Request.Limits.MaximumElapsedMilliseconds - State.ElapsedMilliseconds;
        var invocationRemaining = scope.Allowance.RemainingMilliseconds;
        var runtimeDuration = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(targetRemaining, invocationRemaining)));
        var invocationOwnsDuration = invocationRemaining <= targetRemaining
            && runtimeDuration >= TimeSpan.FromMilliseconds(invocationRemaining);
        var request = new AgentRequest(Guid.NewGuid(), Instructions,
            [new(AgentInputSource.Repository, JsonSerializer.Serialize(prompt))],
            // Product checkpoints/Scope deny effects if the deadline expires during request construction.
            new(maximum, runtimeDuration),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation
                | AgentCapability.DispatchLimits | AgentCapability.UsageReporting,
            new AgentUsageLimits(maximumPhysicalDispatches: maximum,
                maximumToolInvocations: Math.Max(1, State.Request.Limits.MaximumToolCalls),
                retryPolicy: new(Math.Max(1, Math.Min(maximum,
                    State.Request.Limits.MaximumAttempts - scope.RestoredRetryableProviderFailures)))));
        try
        {
            RuntimeTask = runtime.ExecuteCandidatesAsync(new(request, new(maximum,
                Math.Max(0, DocumentationScribeValidation.MaximumInvalidSubmissions - State.RepairCount), Math.Max(0, maximum - 1),
                DocumentationScribeContract.MaximumArtifactUtf8Bytes)), this, cancellationToken: token).AsTask();
            RuntimeResult = await RuntimeTask.ConfigureAwait(false);
        }
        finally
        {
            await DrainAsync();
            // Drain the bounded product observation/settlement, not an interrupted
            // original Host callback: it may still have effects requiring reconciliation.
            try { await hostPending; }
            catch (OperationCanceledException) { }
            catch (ToolContractException) when (Result is not null) { }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { ProviderFault = true; }
        }
        State.ProviderRequestCount = scope.PhysicalProviderRequestCount;
        // A runtime cut or normalized provider exception cannot certify product settlement/acceptance.
        if (token.IsCancellationRequested) return new DocumentationScribeTerminalReducer().CommitCancelled(State, token);
        if (scope.Failed || ProviderFault) return new DocumentationScribeTerminalReducer().CommitInternal(State, token);
        if (RuntimeResult?.StopReason == CandidateStopReason.DurationLimit)
        {
            // A local SDK cut also owns the deadline when only Host completion was pending;
            // provider settlement is not guaranteed to have observed this invocation boundary.
            if (invocationOwnsDuration) _ = scope.Allowance.CanBeginProviderWork();
            return new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Timeout);
        }
        if (Result is null && RuntimeResult?.StopReason is CandidateStopReason.WorkUnitLimit
            or CandidateStopReason.SubmissionLimit or CandidateStopReason.ContinuationLimit)
            return new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        if (Result is null && RuntimeResult?.StopReason == CandidateStopReason.RepairLimit)
            return new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.repair.exhausted");
        if (Result is null && RuntimeResult?.StopReason == CandidateStopReason.RuntimeLimit)
            return new DocumentationScribeTerminalReducer().CommitFinish(State, token, contextStop: true);
        if (Result is null && State.ProviderRequestCount == 0)
            return new DocumentationScribeTerminalReducer().CommitInternal(State, token);
        return Result ?? new DocumentationScribeTerminalReducer().CommitProvider(State, token,
            providerDisposition);
    }

    private async Task<bool> DrainAsync()
    {
        try { await provider.Pending; }
        catch (ProviderFailureException) { }
        catch (ProviderContractException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { ProviderFault = true; }
        return !ProviderFault && !State.Scope.Failed;
    }

    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        if (!State.Scope.Failed)
        {
            Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
            if (Result is null && (!State.Scope.Allowance.CanBeginProviderWork()
                || State.ProviderRequestCount >= State.Request.Limits.MaximumProviderRequests
                || !State.CanStartAdditionalModelWork))
                Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        }
        return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
            State.Scope.Failed || Result is not null ? ExposureDecision.Deny : ExposureDecision.Permit, ExposureStrength.Volatile));
    }

    public async ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        // Solus may seal its numeric channel before the actual scoped provider task has settled.
        var settled = await DrainAsync().WaitAsync(token);
        var stopped = retryableFailure && (State.Scope.RestoredRetryableProviderFailures + observedRetryFailures >= State.Request.Limits.MaximumAttempts
            || !State.Scope.Allowance.CanBeginProviderWork());
        return settled ? new(settlement.Exposure, RuntimeHookStatus.Acknowledged, stopped ? RuntimeContinuation.Stop : RuntimeContinuation.Continue,
            settlement.Accounting) : new(settlement.Exposure, RuntimeHookStatus.Failed);
    }

    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken token = default)
    {
        var pending = SubmitProductAsync(submission, token);
        hostPending = pending;
        return new(pending);
    }

    private async Task WaitForProductRetryAsync(CancellationToken token)
    {
        // This completes only the product's persisted Host wait. The SDK alone admits retries.
        while (State.Scope.PendingRetryAfterMilliseconds > 0)
        {
            var pendingBefore = State.Scope.PendingRetryAfterMilliseconds;
            var permit = await State.Scope.BeginHostAsync(DocumentationScribeHostOperation.RetryWait, token);
            if (permit is null) throw new OperationCanceledException(token);
            try
            {
                var begun = permit.TryBeginOperation();
                if (!begun && (permit.RemainingMilliseconds > 0 || State.Scope.Failed || State.Scope.Allowance.HasCheckedStop
                    || permit.DeadlineOwner != DocumentationScribeDeadlineOwner.HostOperation))
                {
                    State.Scope.ObserveDeadline(permit);
                    Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
                    throw new OperationCanceledException(token);
                }
                // Reservation/readback already spent part or all of this finite interval.
                // A zero remaining Host-owned wait still requires mandatory completion.
                var remaining = permit.RemainingMilliseconds;
                if (remaining > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(remaining), State.Scope.Allowance.Clock, token).ConfigureAwait(false);
                if (permit.DeadlineOwner != DocumentationScribeDeadlineOwner.HostOperation)
                {
                    State.Scope.ObserveDeadline(permit);
                    Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
                    throw new OperationCanceledException(token);
                }
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested) State.Scope.ObserveDeadline(permit);
                throw;
            }
            finally
            {
                if (!await State.Scope.CompleteHostAsync(permit, CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("scribe.retry.settlement-unconfirmed");
            }
            if (State.Scope.PendingRetryAfterMilliseconds >= pendingBefore)
            {
                Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
                throw new ProviderContractException(ProviderError.LimitExceeded);
            }
        }
    }

    private DocumentationScribeModelRequest ProductRequest(ProviderRequest request, CancellationToken token)
    {
        DocumentationScribePromptInput? received = null;
        if (request.Inputs.Count >= 2 && request.Inputs[0].Kind == ProviderInputKind.HostInstruction
            && request.Inputs[0].Text == Instructions && request.Inputs[1].Kind == ProviderInputKind.InputData)
        {
            try { received = JsonSerializer.Deserialize<DocumentationScribePromptInput>(request.Inputs[1].Text!); }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
            { received = null; }
        }
        if (received is null || !DocumentationScribePromptBuilder.IsPromptInputValid(State.Request, received))
        {
            Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.prompt.invalid");
            throw new ProviderContractException(ProviderError.InvalidInput);
        }
        var completed = ImmutableArray.CreateBuilder<DocumentationScribeCompletedToolExchange>();
        foreach (var input in request.Inputs.Skip(2))
        {
            if (input.Model is { Finish: ProviderFinish.Final } final)
            {
                if (!terminalFrames.TryGetValue(final.Attempt.PhysicalAttemptId, out var frame)
                    || final.Text != Encoding.UTF8.GetString(frame.Bytes.Span))
                    throw new ProviderContractException(ProviderError.InvalidResponse);
                if (frame.Epoch == epoch && frame.Acknowledgement is { } acknowledgement)
                    completed.Add(new(0, frame.CallId, DocumentationScribeBoundary.TerminalOperationId,
                        frame.Bytes.ToArray().ToImmutableArray(), "complete", acknowledgement, [], frame.Continuation));
                continue;
            }
            if (input.Kind == ProviderInputKind.InputData)
            {
                if (!terminalFrames.Values.Any(frame => frame.Acknowledgement is { } acknowledgement
                    && input.Text == Encoding.UTF8.GetString(acknowledgement.AsSpan())))
                    throw new ProviderContractException(ProviderError.InvalidInput);
                continue;
            }
            if (input.ToolResult is not { } result) continue;
            if (!results.TryGetValue(result.Call.CallId, out var trusted) || trusted.Epoch != epoch) continue;
            if (Encoding.UTF8.GetString(trusted.Exchange.ArgumentsUtf8Json.Span) != Unwrap(result.Call.ArgumentsJson)
                || Encoding.UTF8.GetString(trusted.Exchange.ResultUtf8Json.Span) != Unwrap(result.Json!))
                throw new ProviderContractException(ProviderError.InvalidResponse);
            completed.Add(trusted.Exchange);
        }
        State.CompletedToolExchanges = completed.ToImmutable();
        var output = Math.Min(State.RemainingOutputTokens, State.Scope.Allowance.Limits.MaximumRequestOutputTokens);
        var product = DocumentationScribePromptBuilder.BuildRequest(State.Request, State.AttemptId, received, registry,
            State.AttemptNumber, State.Scope.PhysicalProviderRequestCount + 1, State.ToolCallCount, output, State.CompletedToolExchanges,
            State.Submissions.Values.ToImmutableArray(), State.TargetFailures.Values.ToImmutableArray(), State.NextSequence, State.RepairCount,
            State.RestoredClosedTargets.Order(StringComparer.Ordinal).ToImmutableArray());
        return product;
    }

    private sealed class ScribeProvider(DocumentationScribeSession owner, IDocumentationScribeModelExchange exchange, DocumentationScribeRuntimeOptions options)
        : ModelProvider(new(options.ProviderConfigurationId, options.ModelConfigurationId), ProviderCapabilities.ToolCalls | ProviderCapabilities.UsageReporting)
    {
        internal Task<ProviderResponse> Pending { get; private set; } = Task.FromResult<ProviderResponse>(null!);
        protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
        {
            Pending = SendAsync(request, observation, token);
            return new(Pending);
        }
        private async Task<ProviderResponse> SendAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
        {
            try
            {
                await owner.WaitForProductRetryAsync(token);
                token.ThrowIfCancellationRequested();
                // A failure observation alone does not start the next product attempt or retire its history.
                if (owner.retryAdvancePending)
                {
                    owner.State.AttemptNumber++;
                    owner.epoch++;
                    owner.calls = owner.calls.Clear();
                    owner.results = owner.results.Clear();
                    owner.terminalBytes = null;
                    owner.State.CompletedToolExchanges = [];
                    owner.State.ActiveDynamicEvidenceReferences = [];
                    owner.continuation = null;
                    foreach (var frame in owner.terminalFrames.Values) frame.Continuation = null;
                    owner.batch = null;
                    owner.retryAdvancePending = false;
                }
                owner.retryableFailure = false;
                // Until the scoped operation completes, HTTP may have begun even if a runtime cut
                // prevents this callback from reporting again. Keep the channel conservatively Unknown.
                var before = owner.State.Scope.PhysicalProviderRequestCount;
                var product = owner.ProductRequest(request, token);
                var response = await exchange.SendAsync(product, owner.State.Scope, token);
                owner.State.ProviderRequestCount = owner.State.Scope.PhysicalProviderRequestCount;
                var validObservations = owner.State.TryApplyObservations(response);
                try
                {
                    observation.ObserveDispatch(before < owner.State.ProviderRequestCount ? DispatchExposure.Dispatched : DispatchExposure.NotDispatched);
                    IReadOnlyList<ProviderTokenCounter> counters = response.Usage is { } usage
                        ? [new(ProviderTokenCounterKind.CacheRead, usage.CachedInputTokens, TokenCounterRelationship.IncludedInInput),
                            new(ProviderTokenCounterKind.UncachedInput, usage.UncachedInputTokens, TokenCounterRelationship.IncludedInInput),
                            new(ProviderTokenCounterKind.Reasoning, usage.ReasoningTokens, TokenCounterRelationship.IncludedInOutput)] : [];
                    observation.CaptureUsage(new(response.Usage?.InputTokens, response.Usage?.OutputTokens, counters));
                }
                catch (ProviderContractException) when (token.IsCancellationRequested) { }
                token.ThrowIfCancellationRequested();
                owner.Result = new DocumentationScribeTerminalReducer().TryCommitPriority(owner.State, token,
                    allowReturnedTerminal: validObservations && response.TerminalSubmissions.Length == 1
                        && response.ToolCalls.IsEmpty && response.Failure is null);
                if (owner.Result is not null) throw new ProviderContractException(ProviderError.LimitExceeded);
                if (!validObservations)
                {
                    owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, "provider-response.invalid");
                    throw new ProviderContractException(ProviderError.InvalidResponse);
                }
                if (Convert.ToInt32(response.Failure is not null) + Convert.ToInt32(!response.ToolCalls.IsEmpty)
                    + Convert.ToInt32(!response.TerminalSubmissions.IsEmpty) != 1 || response.TerminalSubmissions.Length > 1)
                {
                    owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, "provider-response.conflict");
                    throw new ProviderContractException(ProviderError.InvalidResponse);
                }
                if (response.Failure is { } failure)
                {
                    owner.providerDisposition = failure.IsTransient ? DocumentationScribeProviderFinalDisposition.Retryable
                        : DocumentationScribeProviderFinalDisposition.Terminal;
                    if (failure.Code == DocumentationScribeModelFailureCode.ContextLimit
                        && failure.Origin == DocumentationScribeModelFailureOrigin.RequestPreparation)
                        owner.Result = new DocumentationScribeTerminalReducer().CommitFinish(owner.State, token, contextStop: true);
                    else if (failure.Origin == DocumentationScribeModelFailureOrigin.RequestPreparation)
                        owner.Result = new DocumentationScribeTerminalReducer().CommitValidation(owner.State, token, "scribe.provider.not-sent");
                    if (failure.IsTransient)
                    {
                        owner.retryableFailure = true;
                        owner.observedRetryFailures++;
                        owner.retryAdvancePending = true;
                    }
                    throw new ProviderFailureException(failure.IsTransient ? new ProviderRetry(
                        failure.Code == DocumentationScribeModelFailureCode.RateLimited ? ProviderRetryKind.Throttled : ProviderRetryKind.Transient) : null);
                }
                if (response.TerminalSubmissions.Length == 1 && response.ToolCalls.IsEmpty)
                {
                    // Keep the original bytes authoritative for independent Core validation.
                    // The SDK string must not repair malformed UTF-8 or normalize valid JSON.
                    owner.terminalBytes = response.TerminalSubmissions[0].TerminalUtf8Json;
                    var callId = response.TerminalSubmissions[0].CallId ?? "call.scribe." + owner.State.ProviderRequestCount;
                    owner.pendingTerminalFrame = new(request.Attempt.PhysicalAttemptId, owner.epoch, callId,
                        owner.terminalBytes.Value, response.AssistantContinuation);
                    owner.terminalFrames = owner.terminalFrames.Add(request.Attempt.PhysicalAttemptId, owner.pendingTerminalFrame);
                    return new(request.Scope, request.Attempt, ProviderFinish.Final,
                        Encoding.UTF8.GetString(owner.terminalBytes.Value.Span), []);
                }
                if (!response.TerminalSubmissions.IsEmpty || response.ToolCalls.IsEmpty)
                {
                    owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, "provider-response.conflict");
                    throw new ProviderContractException(ProviderError.InvalidResponse);
                }
                if (response.ToolCalls.Length > owner.State.Scope.Allowance.Limits.MaximumToolCallsPerResponse
                    || response.ToolCalls.Length > owner.State.Scope.Allowance.RemainingToolCalls
                    || response.ToolCalls.Length > owner.State.Request.Limits.MaximumToolCalls - owner.State.ToolCallCount
                    || owner.State.ToolRoundCount >= owner.State.Request.Limits.MaximumToolRounds)
                {
                    owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget);
                    throw new ProviderContractException(ProviderError.LimitExceeded);
                }
                var counts = new int[owner.registry.Registrations.Length];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var mapped = new List<ToolCall>();
                foreach (var call in response.ToolCalls)
                {
                    var index = owner.registry.FindRegistrationIndex(call.OperationId);
                    if (index < 0 || call.ResponseIndex != mapped.Count || !seen.Add(call.CallId))
                    {
                        owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, "tool-call.rejected");
                        throw new ProviderContractException(ProviderError.InvalidResponse);
                    }
                    if (owner.State.PerOperationToolCalls[index] + ++counts[index] > owner.registry.Registrations[index].MaximumCallsPerRun)
                    {
                        owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget);
                        throw new ProviderContractException(ProviderError.LimitExceeded);
                    }
                    // Complete product-domain preparation before any member is invoked by the runtime.
                    _ = owner.registry.Registrations[index].Prepare(call);
                    // Product retries start fresh history and may reuse provider call IDs. Keep runtime IDs
                    // unique across its retained inventory without changing original wire/C4 call identity.
                    var runtimeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        "scribe/tool-call/v1\n" + request.Attempt.PhysicalAttemptId.ToString("N") + "\n" + call.CallId))).ToLowerInvariant();
                    owner.calls = owner.calls.Add(runtimeId, call);
                    mapped.Add(new(runtimeId, RuntimeToolName(index), Wrap(Encoding.UTF8.GetString(call.ArgumentsUtf8Json.Span))));
                }
                owner.State.ToolRoundCount++;
                owner.continuation = response.AssistantContinuation;
                owner.batch = new(owner.epoch, mapped.Count, owner.State.ActiveDynamicEvidenceReferences);
                return new(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null, mapped);
            }
            catch (ProviderFailureException) { throw; }
            catch (ProviderContractException) { throw; }
            catch (PromptBoundaryException)
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitFinish(owner.State, token, contextStop: true);
                throw new ProviderContractException(ProviderError.LimitExceeded);
            }
            catch (ToolProtocolException exception)
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, exception.ReferenceId);
                throw new ProviderContractException(ProviderError.InvalidResponse);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { owner.ProviderFault = true; throw; }
        }
    }

    private sealed class ToolBatch(int epoch, int count, ImmutableArray<DocumentationScribeEvidenceReference> active)
    {
        internal int Epoch { get; } = epoch;
        internal int Count { get; } = count;
        internal ImmutableDictionary<string, DocumentationScribeEvidenceReference> ProspectiveActive { get; set; } =
            active.ToImmutableDictionary(reference => reference.EvidenceReferenceId, StringComparer.Ordinal);
        internal List<(string RuntimeId, DocumentationScribeCompletedToolExchange Exchange)> Completed { get; } = [];
    }

    private sealed class ReadCapability : IToolCapability
    {
        public string CapabilityId => "scribe_read";
    }

    private sealed class ReadTool(DocumentationScribeSession owner, ToolRegistration registration, string runtimeName)
        : FunctionTool<ReadCapability>(new(runtimeName, "Read authorized Scribe evidence.",
            ToolSchema.Parse(EnvelopeSchema), ToolSchema.Parse(EnvelopeSchema), "scribe_read", ToolEffect.ReadOnly,
            maximumArgumentBytes: ToolLimits.MaximumPayloadBytes, maximumResultBytes: ToolLimits.MaximumPayloadBytes))
    {
        protected override bool ValidateArguments(JsonElement arguments)
        {
            _ = registration.Prepare(new(0, "scribe.prepare", registration.OperationId,
                Encoding.UTF8.GetBytes(arguments.GetProperty("payload").GetString()!)));
            return true;
        }
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, ReadCapability capability, CancellationToken token)
        {
            var pending = InvokeProductAsync(call, token);
            owner.hostPending = pending;
            return new(pending);
        }

        private async Task<ToolOutput> InvokeProductAsync(ToolCall call, CancellationToken token)
        {
            try { return await InvokeProductCoreAsync(call, token).ConfigureAwait(false); }
            catch (ToolProtocolException exception)
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, exception.ReferenceId);
                throw new ToolContractException(ToolError.InvalidResult);
            }
        }

        private async Task<ToolOutput> InvokeProductCoreAsync(ToolCall call, CancellationToken token)
        {
            var original = owner.calls[call.CallId];
            if (Encoding.UTF8.GetString(original.ArgumentsUtf8Json.Span) != Unwrap(call.ArgumentsJson))
                throw new ToolContractException(ToolError.InvalidArguments);
            var phase = original.OperationId is "read-excerpt" or "list-files" or "search-text"
                ? DocumentationScribeHostOperation.RepositoryTool : original.OperationId == "get-target-evidence"
                    ? DocumentationScribeHostOperation.SemanticTool : DocumentationScribeHostOperation.RegisteredTool;
            var permit = await owner.State.Scope.BeginHostAsync(phase, token);
            if (permit is null)
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget);
                throw new ToolContractException(ToolError.InvocationFailed);
            }
            ToolInvocationResult? result = null;
            var timedOut = false;
            try
            {
                if (!permit.TryBeginOperation())
                {
                    owner.State.Scope.ObserveDeadline(permit);
                    timedOut = true;
                }
                else
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(permit.RemainingMilliseconds), owner.State.Scope.Allowance.Clock);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                    try
                    {
                        owner.State.ToolCallCount++;
                        owner.State.PerOperationToolCalls[owner.registry.FindRegistrationIndex(registration.OperationId)]++;
                        var invocation = registration.Prepare(original).InvokeAsync(linked.Token).AsTask();
                        try { result = await invocation.WaitAsync(linked.Token); }
                        catch (OperationCanceledException) when (!invocation.IsCompleted)
                        { _ = DocumentationScribeTaskObservation.ObserveLateAsync(invocation); throw; }
                        linked.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        owner.State.Scope.ObserveDeadline(permit);
                        timedOut = true;
                    }
                }
            }
            finally
            {
                if (!await owner.State.Scope.CompleteHostAsync(permit, CancellationToken.None))
                    throw new InvalidOperationException("scribe.tool.settlement-unconfirmed");
            }

            // Reduction follows mandatory completion so its current resource observations win.
            owner.Result = new DocumentationScribeTerminalReducer().TryCommitPriority(owner.State, token);
            if (owner.Result is null && timedOut)
                owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token,
                    owner.State.Scope.ResourceBudgetReached ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
            if (owner.Result is not null) throw new ToolContractException(ToolError.InvocationFailed);
            var returned = result ?? throw new InvalidOperationException("scribe.tool.result-missing");
            if (returned.Outcome == DocumentationScribeToolOutcome.BudgetExhausted
                || returned.Outcome == DocumentationScribeToolOutcome.TimedOut
                || returned.Outcome == DocumentationScribeToolOutcome.Cancelled
                || returned.Outcome == DocumentationScribeToolOutcome.Failure)
            {
                owner.Result = returned.Outcome == DocumentationScribeToolOutcome.BudgetExhausted
                    ? new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget)
                    : returned.Outcome == DocumentationScribeToolOutcome.TimedOut
                        ? new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Timeout)
                        : token.IsCancellationRequested
                            ? new DocumentationScribeTerminalReducer().CommitCancelled(owner.State, token)
                            : new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, original.OperationId);
                throw new ToolContractException(ToolError.InvocationFailed);
            }
            if (returned.Outcome == DocumentationScribeToolOutcome.Unavailable && !returned.DynamicEvidence.IsEmpty)
                throw RejectEvidence();
            var current = owner.batch ?? throw new InvalidOperationException("scribe.tool.batch-missing");
            if (current.Epoch != owner.epoch) throw new InvalidOperationException("scribe.tool.batch-stale");
            var references = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceReference>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in returned.DynamicEvidence)
            {
                if (!DocumentationScribeValidation.TryCreateDynamicEvidenceReference(owner.State.Request, input, out var created)
                    || created is null || !ids.Add(created.EvidenceReferenceId)) throw RejectEvidence();
                DocumentationScribeEvidenceReference accepted;
                var originalEvidence = owner.State.Request.EvidenceReferences.FirstOrDefault(reference =>
                    reference.EvidenceReferenceId == created.EvidenceReferenceId);
                if (originalEvidence is not null)
                {
                    if (!EvidenceReferenceEquivalent(originalEvidence, created)) throw RejectEvidence();
                    accepted = originalEvidence;
                }
                else if (owner.State.ChargedDynamicEvidenceById.TryGetValue(created.EvidenceReferenceId, out var prior))
                {
                    if (!EvidenceReferenceEquivalent(prior, created)) throw RejectEvidence();
                    accepted = prior;
                    current.ProspectiveActive = current.ProspectiveActive.SetItem(accepted.EvidenceReferenceId, accepted);
                }
                else
                {
                    accepted = created;
                    current.ProspectiveActive = current.ProspectiveActive.Add(accepted.EvidenceReferenceId, accepted);
                    if (!owner.State.TryChargeDynamicEvidence(accepted)) throw ExhaustedEvidenceBudget();
                }
                references.Add(accepted);
            }
            var completed = new DocumentationScribeCompletedToolExchange(original.ResponseIndex, original.CallId,
                original.OperationId, original.ArgumentsUtf8JsonStorage, returned.Outcome.Id, returned.ResultUtf8Json,
                references.OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray(),
                original.ResponseIndex == 0 ? owner.continuation : null);
            if (!owner.State.TryChargeSuccessfulToolExchange(DocumentationScribePromptBuilder.MeasureCompletedToolExchange(completed))
                || owner.State.IsEvidenceBudgetExceeded) throw ExhaustedEvidenceBudget();
            current.Completed.Add((call.CallId, completed));
            if (current.Completed.Count == current.Count)
            {
                owner.Result = new DocumentationScribeTerminalReducer().TryCommitPriority(owner.State, token);
                if (owner.Result is not null) throw new ToolContractException(ToolError.InvocationFailed);
                owner.State.ActiveDynamicEvidenceReferences = current.ProspectiveActive.Values
                    .OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray();
                owner.State.CompletedToolExchanges = owner.State.CompletedToolExchanges.AddRange(current.Completed.Select(item => item.Exchange));
                foreach (var item in current.Completed) owner.results = owner.results.Add(item.RuntimeId, (owner.epoch, item.Exchange));
                owner.batch = null;
            }
            return ToolOutput.Success(call, Wrap(Encoding.UTF8.GetString(returned.ResultUtf8Json.AsSpan())));

            ToolContractException RejectEvidence()
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, original.OperationId);
                return new(ToolError.InvalidResult);
            }
            ToolContractException ExhaustedEvidenceBudget()
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget);
                return new(ToolError.LimitExceeded);
            }
        }
    }

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

    // Registry ordinals are unique and preserve product operation identity without lossy punctuation replacement.
    private static string RuntimeToolName(int index) => "scribe_tool_" + index.ToString("D3", CultureInfo.InvariantCulture);
    private static string Wrap(string payload) => JsonSerializer.Serialize(new { payload });
    private static string Unwrap(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("payload").GetString()!;
    }
}
