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
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace ContractScribe.Tests;

// A public-package qualification fixture. The production loop remains unchanged until #244.
// Product state/codecs are real. Tests identify controlled HTTP, port and checkpoint seams;
// the native repository cases use the actual confined read port.
internal sealed class SolusScribeQualificationSession : ICandidateHost, IRuntimeExposureHooks
{
    private const string Instructions = "Execute the Host-bound Documentation Scribe protocol using only registered read tools.";
    private const string EnvelopeSchema = "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"payload\"],\"properties\":{\"payload\":{\"type\":\"string\"}}}";
    private readonly DocumentationScribeToolRegistry registry;
    private readonly DocumentationScribePromptInput prompt;
    private readonly ICandidateAgent runtime;
    private readonly QualificationProvider provider;
    private readonly Dictionary<string, (int Epoch, DocumentationScribeCompletedToolExchange Exchange)> results = [];
    private readonly Dictionary<string, DocumentationScribeModelToolCall> calls = [];
    private DocumentationScribeAssistantContinuation? continuation;
    private int epoch;
    private int observedRetryFailures;
    private bool retryAdvancePending;
    private bool retryableFailure;
    private DocumentationScribeProviderFinalDisposition providerDisposition = DocumentationScribeProviderFinalDisposition.Terminal;
    private Task hostPending = Task.CompletedTask;
    private ToolBatch? batch;
    internal readonly RunState State;
    internal readonly List<DocumentationScribeModelRequest> Requests = [];
    internal CandidateExecutionResult? RuntimeResult { get; private set; }
    internal DocumentationScribeRunResult? Result { get; private set; }
    internal bool ProviderFault { get; private set; }
    internal bool DenyHook { get; set; }
    internal Task ProviderCompletion => provider.Pending;
    internal Task HostCompletion => hostPending;
    internal int HookSettlements { get; private set; }
    internal TaskCompletionSource RuntimeReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SolusScribeQualificationSession(DocumentationScribeRequest request, DocumentationScribeAttemptId attempt,
        DocumentationScribePromptInput prompt, DocumentationScribeToolRegistry registry,
        IDocumentationScribeModelExchange exchange, DocumentationScribeExecutionScope scope)
    {
        this.registry = registry; this.prompt = prompt;
        if (registry.ToolPolicyId != request.ToolPolicyId || !DocumentationScribePromptBuilder.IsPromptInputValid(request, prompt))
            throw new ArgumentException("The qualification input is not bound to the admitted request.");
        State = new(request, attempt, new("provider.synthetic.v1", "model.synthetic.v1", "scribe-protocol.v1"), registry, scope.Allowance.Clock, scope);
        provider = new(this, exchange);
        var tools = registry.Registrations.Select((registration, index) =>
            new RuntimeToolRegistration(new ReadTool(this, registration, RuntimeToolName(index)), new ReadCapability())).ToArray();
        var configuration = new RuntimeConfiguration(provider, tools, this, Bounds());
        runtime = (ICandidateAgent)RuntimeAgentFactory.Create(configuration,
            new RuntimeOptions(scope.Allowance.Clock, maximumAttempts: 128, maximumRecords: 2048,
                maximumRetainedBytes: RuntimeOptions.MaximumRetainedCapacityBytes));
    }

    internal static ProviderExchangeBounds Bounds() => new(maximumInputs: 2048,
        maximumRequestBytes: ProviderLimits.MaximumRequestBytes, maximumResponseBytes: ProviderLimits.MaximumResponseBytes,
        maximumContinuationBytes: ProviderLimits.MaximumContinuationBytes, maximumTextBytes: ProviderLimits.MaximumTextBytes);

    internal async Task<DocumentationScribeRunResult> RunAsync(CancellationToken token = default, TimeSpan? duration = null)
    {
        var scope = State.Scope;
        Result = new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
        if (Result is null && !scope.Allowance.CanBeginProviderWork())
            Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        if (Result is not null)
        {
            RuntimeReturned.TrySetResult();
            return Result;
        }
        var maximum = Math.Min(128, Math.Min(State.Request.Limits.MaximumProviderRequests, scope.Allowance.Limits.MaximumProviderRequests));
        var request = new AgentRequest(Guid.NewGuid(), Instructions,
            [new(AgentInputSource.Repository, JsonSerializer.Serialize(prompt))],
            // Product checkpoints/Scope deny effects if the deadline expires during request construction.
            new(maximum, duration ?? TimeSpan.FromMilliseconds(Math.Max(1, State.RemainingMilliseconds))),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation
                | AgentCapability.DispatchLimits | AgentCapability.UsageReporting,
            new AgentUsageLimits(maximumPhysicalDispatches: maximum,
                maximumToolInvocations: Math.Max(1, State.Request.Limits.MaximumToolCalls),
                retryPolicy: new(Math.Max(1, Math.Min(maximum,
                    State.Request.Limits.MaximumAttempts - scope.RestoredRetryableProviderFailures)))));
        try
        {
            RuntimeResult = await runtime.ExecuteCandidatesAsync(new(request, new(1, 0, 0,
                DocumentationScribeContract.MaximumArtifactUtf8Bytes)), this, cancellationToken: token);
            RuntimeReturned.TrySetResult();
        }
        finally
        {
            await DrainAsync();
            try { await hostPending; }
            catch (OperationCanceledException) { }
            catch (ToolContractException) when (Result is not null) { }
            catch (Exception) { ProviderFault = true; }
        }
        State.ProviderRequestCount = scope.PhysicalProviderRequestCount;
        // A runtime cut or normalized provider exception cannot certify product settlement/acceptance.
        if (token.IsCancellationRequested) return new DocumentationScribeTerminalReducer().CommitCancelled(State, token);
        if (scope.Failed || ProviderFault) return new DocumentationScribeTerminalReducer().CommitInternal(State, token);
        if (RuntimeResult?.StopReason == CandidateStopReason.DurationLimit)
            return new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Timeout);
        if (Result is null && RuntimeResult?.StopReason == CandidateStopReason.WorkUnitLimit)
            return new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
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
        catch (Exception) { ProviderFault = true; }
        return !ProviderFault && !State.Scope.Failed;
    }

    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        if (!DenyHook && !State.Scope.Failed)
        {
            Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
            if (Result is null && (!State.Scope.Allowance.CanBeginProviderWork()
                || State.ProviderRequestCount >= State.Request.Limits.MaximumProviderRequests
                || State.ProviderRequestCount > 0 && !State.CanStartAdditionalModelWork))
                Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        }
        return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
            DenyHook || State.Scope.Failed || Result is not null ? ExposureDecision.Deny : ExposureDecision.Permit, ExposureStrength.Volatile));
    }

    public async ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        // Solus may seal its numeric channel before the actual scoped provider task has settled.
        var settled = await DrainAsync().WaitAsync(token);
        HookSettlements++;
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

    private async Task<CandidateFeedback?> SubmitProductAsync(CandidateSubmission submission, CancellationToken token)
    {
        if (!await DrainAsync()) return new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Failed);
        Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token, allowReturnedTerminal: true);
        if (Result is not null)
            return new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged,
                CandidateDecision.Reject, CandidateContinuation.End);
        var permit = await State.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, token);
        if (permit is null || !permit.TryBeginOperation())
            Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
        else
        {
            try
            {
                State.ToolCallCount++;
                Result = new DocumentationScribeTerminalReducer().CommitTerminal(State, token, Encoding.UTF8.GetBytes(submission.Payload));
            }
            finally
            {
                if (!await State.Scope.CompleteHostAsync(permit, CancellationToken.None))
                    throw new InvalidOperationException("qualification.terminal.settlement-unconfirmed");
            }
        }
        return new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged,
            Result.Terminal.Kind is DocumentationScribeTerminalKind.Proposal or DocumentationScribeTerminalKind.Skip
                ? CandidateDecision.Accept : CandidateDecision.Reject, CandidateContinuation.End);
    }

    private async Task WaitForProductRetryAsync(CancellationToken token)
    {
        if (State.Scope.PendingRetryAfterMilliseconds <= 0) return;
        var permit = await State.Scope.BeginHostAsync(DocumentationScribeHostOperation.RetryWait, token);
        if (permit is null || !permit.TryBeginOperation()) throw new OperationCanceledException(token);
        // A normal retry-wait grant represents the planned wait itself. Only a shorter resource
        // deadline interrupts it; using the planned delay as a competing timer would race fulfillment.
        using var deadline = permit.MaximumMilliseconds < State.Scope.PendingRetryAfterMilliseconds
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(permit.RemainingMilliseconds), State.Scope.Allowance.Clock)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { await Task.Delay(TimeSpan.FromMilliseconds(State.Scope.PendingRetryAfterMilliseconds), State.Scope.Allowance.Clock, linked.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            State.Scope.ObserveDeadline(permit);
            Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
            throw;
        }
        finally
        {
            if (!await State.Scope.CompleteHostAsync(permit, CancellationToken.None))
                throw new InvalidOperationException("qualification.retry.settlement-unconfirmed");
        }
    }

    private DocumentationScribeModelRequest ProductRequest(ProviderRequest request)
    {
        Assert.Equal(ProviderInputKind.HostInstruction, request.Inputs[0].Kind);
        Assert.Equal(Instructions, request.Inputs[0].Text);
        Assert.Equal(ProviderInputKind.InputData, request.Inputs[1].Kind);
        var received = JsonSerializer.Deserialize<DocumentationScribePromptInput>(request.Inputs[1].Text!)!;
        Assert.True(DocumentationScribePromptBuilder.IsPromptInputValid(State.Request, received));
        var completed = ImmutableArray.CreateBuilder<DocumentationScribeCompletedToolExchange>();
        foreach (var input in request.Inputs.Where(input => input.ToolResult is not null))
        {
            var result = input.ToolResult!;
            if (!results.TryGetValue(result.Call.CallId, out var trusted) || trusted.Epoch != epoch) continue;
            Assert.Equal(Encoding.UTF8.GetString(trusted.Exchange.ArgumentsUtf8Json.Span), Unwrap(result.Call.ArgumentsJson));
            Assert.Equal(Encoding.UTF8.GetString(trusted.Exchange.ResultUtf8Json.Span), Unwrap(result.Json!));
            completed.Add(trusted.Exchange);
        }
        State.CompletedToolExchanges = completed.ToImmutable();
        var output = Math.Min(State.RemainingOutputTokens, State.Scope.Allowance.Limits.MaximumRequestOutputTokens);
        var product = DocumentationScribePromptBuilder.BuildRequest(State.Request, State.AttemptId, received, registry,
            State.AttemptNumber, State.Scope.PhysicalProviderRequestCount + 1, State.ToolCallCount, output, State.CompletedToolExchanges);
        Requests.Add(product);
        return product;
    }

    private sealed class QualificationProvider(SolusScribeQualificationSession owner, IDocumentationScribeModelExchange exchange)
        : ModelProvider(new("deepseek", "deepseek-v4-flash"), ProviderCapabilities.ToolCalls | ProviderCapabilities.UsageReporting)
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
                    owner.State.CompletedToolExchanges = [];
                    owner.State.ActiveDynamicEvidenceReferences = [];
                    owner.continuation = null;
                    owner.batch = null;
                    owner.retryAdvancePending = false;
                }
                owner.retryableFailure = false;
                // Until the scoped operation completes, HTTP may have begun even if a runtime cut
                // prevents this callback from reporting again. Keep the channel conservatively Unknown.
                var before = owner.State.Scope.PhysicalProviderRequestCount;
                var product = owner.ProductRequest(request);
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
                    if (failure.Origin == DocumentationScribeModelFailureOrigin.RequestPreparation)
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
                    return new(request.Scope, request.Attempt, ProviderFinish.Final,
                        Encoding.UTF8.GetString(response.TerminalSubmissions[0].TerminalUtf8Json.Span), []);
                if (!response.TerminalSubmissions.IsEmpty || response.ToolCalls.IsEmpty)
                {
                    owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, "provider-response.conflict");
                    throw new ProviderContractException(ProviderError.InvalidResponse);
                }
                if (response.ToolCalls.Length > owner.State.Scope.Allowance.RemainingToolCalls
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
                    owner.calls[runtimeId] = call;
                    mapped.Add(new(runtimeId, RuntimeToolName(index), Wrap(Encoding.UTF8.GetString(call.ArgumentsUtf8Json.Span))));
                }
                owner.State.ToolRoundCount++;
                owner.continuation = response.AssistantContinuation;
                owner.batch = new(owner.epoch, mapped.Count, owner.State.ActiveDynamicEvidenceReferences);
                return new(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null, mapped);
            }
            catch (ProviderFailureException) { throw; }
            catch (ProviderContractException) { throw; }
            catch (ToolProtocolException exception)
            {
                owner.Result = new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, exception.ReferenceId);
                throw new ProviderContractException(ProviderError.InvalidResponse);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { owner.ProviderFault = true; throw; }
        }
    }

    private sealed class ToolBatch(int epoch, int count, ImmutableArray<DocumentationScribeEvidenceReference> active)
    {
        internal int Epoch { get; } = epoch;
        internal int Count { get; } = count;
        internal Dictionary<string, DocumentationScribeEvidenceReference> ProspectiveActive { get; } =
            active.ToDictionary(reference => reference.EvidenceReferenceId, StringComparer.Ordinal);
        internal List<(string RuntimeId, DocumentationScribeCompletedToolExchange Exchange)> Completed { get; } = [];
    }

    private sealed class ReadCapability : IToolCapability
    {
        public string CapabilityId => "scribe_read";
    }

    private sealed class ReadTool(SolusScribeQualificationSession owner, ToolRegistration registration, string runtimeName)
        : FunctionTool<ReadCapability>(new(runtimeName, "Read authorized Scribe evidence.",
            ToolSchema.Parse(EnvelopeSchema), ToolSchema.Parse(EnvelopeSchema), "scribe_read", ToolEffect.ReadOnly,
            maximumArgumentBytes: ToolLimits.MaximumPayloadBytes, maximumResultBytes: ToolLimits.MaximumPayloadBytes))
    {
        protected override bool ValidateArguments(JsonElement arguments)
        {
            _ = registration.Prepare(new(0, "qualification.prepare", registration.OperationId,
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
            var original = owner.calls[call.CallId];
            Assert.Equal(Encoding.UTF8.GetString(original.ArgumentsUtf8Json.Span), Unwrap(call.ArgumentsJson));
            var phase = original.OperationId is "read-excerpt" or "list-files" or "search-text"
                ? DocumentationScribeHostOperation.RepositoryTool : original.OperationId == "get-target-evidence"
                    ? DocumentationScribeHostOperation.SemanticTool : DocumentationScribeHostOperation.RegisteredTool;
            var permit = await owner.State.Scope.BeginHostAsync(phase, token);
            if (permit is null || !permit.TryBeginOperation()) throw new OperationCanceledException(token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(permit.RemainingMilliseconds), owner.State.Scope.Allowance.Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            ToolInvocationResult result;
            try
            {
                owner.State.ToolCallCount++;
                owner.State.PerOperationToolCalls[owner.registry.FindRegistrationIndex(registration.OperationId)]++;
                var invocation = registration.Prepare(original).InvokeAsync(linked.Token).AsTask();
                try { result = await invocation.WaitAsync(linked.Token); }
                catch (OperationCanceledException) when (!invocation.IsCompleted)
                { _ = DocumentationScribeRuntime.ObserveLateAsync(invocation); throw; }
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                owner.State.Scope.ObserveDeadline(permit);
                owner.Result = new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token,
                    owner.State.Scope.ResourceBudgetReached ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
                throw new ToolContractException(ToolError.InvocationFailed);
            }
            finally
            {
                if (!await owner.State.Scope.CompleteHostAsync(permit, CancellationToken.None))
                    throw new InvalidOperationException("qualification.tool.settlement-unconfirmed");
            }

            // Product admission follows mandatory Host completion, as in the current runtime.
            owner.Result = new DocumentationScribeTerminalReducer().TryCommitPriority(owner.State, token);
            if (owner.Result is not null) throw new ToolContractException(ToolError.InvocationFailed);
            if (result.Outcome == DocumentationScribeToolOutcome.BudgetExhausted
                || result.Outcome == DocumentationScribeToolOutcome.TimedOut
                || result.Outcome == DocumentationScribeToolOutcome.Cancelled
                || result.Outcome == DocumentationScribeToolOutcome.Failure)
            {
                owner.Result = result.Outcome == DocumentationScribeToolOutcome.BudgetExhausted
                    ? new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Budget)
                    : result.Outcome == DocumentationScribeToolOutcome.TimedOut
                        ? new DocumentationScribeTerminalReducer().CommitFailure(owner.State, token, DocumentationScribeFailureCode.Timeout)
                        : token.IsCancellationRequested
                            ? new DocumentationScribeTerminalReducer().CommitCancelled(owner.State, token)
                            : new DocumentationScribeTerminalReducer().CommitToolProtocol(owner.State, token, original.OperationId);
                throw new ToolContractException(ToolError.InvocationFailed);
            }
            if (result.Outcome == DocumentationScribeToolOutcome.Unavailable && !result.DynamicEvidence.IsEmpty)
                throw RejectEvidence();
            var current = owner.batch ?? throw new InvalidOperationException("qualification.tool.batch-missing");
            if (current.Epoch != owner.epoch) throw new InvalidOperationException("qualification.tool.batch-stale");
            var references = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceReference>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in result.DynamicEvidence)
            {
                if (!DocumentationScribeValidation.TryCreateDynamicEvidenceReference(owner.State.Request, input, out var created)
                    || created is null || !ids.Add(created.EvidenceReferenceId)) throw RejectEvidence();
                DocumentationScribeEvidenceReference accepted;
                var originalEvidence = owner.State.Request.EvidenceReferences.FirstOrDefault(reference =>
                    reference.EvidenceReferenceId == created.EvidenceReferenceId);
                if (originalEvidence is not null)
                {
                    if (!DocumentationScribeValidation.EvidenceReferenceEquivalent(originalEvidence, created)) throw RejectEvidence();
                    accepted = originalEvidence;
                }
                else if (owner.State.ChargedDynamicEvidenceById.TryGetValue(created.EvidenceReferenceId, out var prior))
                {
                    if (!DocumentationScribeValidation.EvidenceReferenceEquivalent(prior, created)) throw RejectEvidence();
                    accepted = prior;
                    current.ProspectiveActive[accepted.EvidenceReferenceId] = accepted;
                }
                else
                {
                    accepted = created;
                    current.ProspectiveActive.Add(accepted.EvidenceReferenceId, accepted);
                    if (!owner.State.TryChargeDynamicEvidence(accepted)) throw ExhaustedEvidenceBudget();
                }
                references.Add(accepted);
            }
            var completed = new DocumentationScribeCompletedToolExchange(original.ResponseIndex, original.CallId,
                original.OperationId, original.ArgumentsUtf8JsonStorage, result.Outcome.Id, result.ResultUtf8Json,
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
                foreach (var item in current.Completed) owner.results[item.RuntimeId] = (owner.epoch, item.Exchange);
                owner.batch = null;
            }
            return ToolOutput.Success(call, Wrap(Encoding.UTF8.GetString(result.ResultUtf8Json.AsSpan())));

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

    // Registry ordinals are unique and preserve product operation identity without lossy punctuation replacement.
    private static string RuntimeToolName(int index) => "scribe_tool_" + index.ToString("D3", CultureInfo.InvariantCulture);
    private static string Wrap(string payload) => JsonSerializer.Serialize(new { payload });
    private static string Unwrap(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("payload").GetString()!;
    }
}
