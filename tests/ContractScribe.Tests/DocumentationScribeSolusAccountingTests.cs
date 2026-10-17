using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(65)]
    [InlineData(128)]
    public async Task Solus_runtime_executes_real_C4_reservation_and_settlement_for_every_retry(int attempts)
    {
        var trial = await SolusTrialAsync(attempts);
        var steps = Enumerable.Range(1, attempts - 1).Select(_ => ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable),
                usage: new(0, 0, 0, 0)))).Append(ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(trial.Scenario))], usage: new(2, 1, 1, 1))));
        var exchange = new ScriptedDocumentationScribeModelExchange(steps.ToImmutableArray());
        var session = trial.Session(exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(attempts, exchange.Requests.Length);
        Assert.Equal(attempts, trial.Store.Dispatches);
        Assert.Equal(attempts, trial.Store.Settlements);
        Assert.Equal(attempts, trial.Coordinator.Scope.PhysicalProviderRequestCount);
        Assert.Equal(attempts - 1, trial.Authority.AcceptedCheckpoint.Artifact.State.ActiveReservation is CampaignProviderReservation reservation
            ? reservation.RetryProgress.RetryableFailureCount : -1);
        Assert.Equal(Enumerable.Range(1, attempts), session.Requests.Select(request => request.AttemptNumber));
        Assert.Equal(Enumerable.Range(1, attempts), session.Requests.Select(request => request.ProviderRequestNumber));
        await trial.CompleteAsync(result);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(1000)]
    public async Task Solus_runtime_retry_readback_consumes_the_admitted_wait_before_the_next_dispatch(int readbackMilliseconds)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 5000, invocationMilliseconds: 1500);
        trial.Store.RetryReadback = () => clock.Advance(readbackMilliseconds);
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                new(DocumentationScribeModelFailureCode.TransientUnavailable, retryAfterMilliseconds: 1000), usage: new(0, 0, 0, 0))),
            ScriptedDocumentationScribeStep.Return(new([], [new(C3ProposalTerminal(trial.Scenario))], usage: new(0, 0, 0, 0)))]);
        var running = trial.Session(exchange).RunAsync();
        await trial.Store.RetryWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(1000 - readbackMilliseconds);
        // Always release an incorrect extra wait so the regression leaves no pending operation.
        if (await Task.WhenAny(running, Task.Delay(200)) != running) clock.Advance(1000);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(2, exchange.Requests.Length);
        Assert.Equal(0, trial.Coordinator.Scope.PendingRetryAfterMilliseconds);
        Assert.Equal(2, result.RunEnvelope.AttemptNumber);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Solus_runtime_retry_exhaustion_stops_at_the_product_limit()
    {
        var trial = await SolusTrialAsync(65);
        var exchange = new ScriptedDocumentationScribeModelExchange(Enumerable.Range(0, 66).Select(_ =>
            ScriptedDocumentationScribeStep.Return(new DocumentationScribeModelResponse([], [],
                new(DocumentationScribeModelFailureCode.TransientUnavailable), usage: new(0, 0, 0, 0)))).ToImmutableArray());
        var session = trial.Session(exchange);
        var result = await session.RunAsync();
        Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(65, exchange.Requests.Length);
        Assert.Equal(65, trial.Store.Dispatches);
        Assert.Equal(65, trial.Store.Settlements);
    }

    [Fact]
    public async Task Solus_runtime_restores_pending_durable_wait_into_a_fresh_host_execution_without_double_charge()
    {
        var trial = await SolusTrialAsync();
        var failed = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable,
                retryAfterMilliseconds: 100), usage: new(0, 0, 0, 0)))]);
        using var cancellation = new CancellationTokenSource();
        var first = trial.Session(failed);
        var running = first.RunAsync(cancellation.Token);
        await trial.Store.RetryWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var interrupted = await running;
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, interrupted.Terminal.Kind);
        Assert.Equal(1, interrupted.RunEnvelope.AttemptNumber);
        Assert.Equal(1, interrupted.RunEnvelope.ProviderRequestCount);
        var current = await CampaignCheckpointAcceptance.AcceptCurrentAsync(trial.Store);
        var saved = Assert.IsType<CampaignProviderReservation>(current.Artifact!.State.ActiveReservation).RetryProgress;
        Assert.InRange(saved.PendingRetryAfterMilliseconds, 1, 100);
        var retired = await CampaignCheckpointAcceptance.AcceptAsync(trial.Store,
            CampaignStateReducer.RetireInterruptedProviderAttempt(current.AcceptedCheckpoint!));
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, retired.Kind);
        // Rehydrate only the actual serialized business checkpoint, with new authority/runtime/provider/state.
        var restoredBytes = retired.Artifact!.ExactUtf8Json.ToArray();
        var restored = CampaignStateJson.Parse(restoredBytes).Artifact!;
        var freshStore = new SolusCheckpointStore(restored);
        var freshReadback = await CampaignCheckpointAcceptance.AcceptCurrentAsync(freshStore);
        var allowance = CampaignInvocationTestPolicy.Create(trial.Request.Limits);
        var transition = CampaignStateReducer.RetryProviderInvocation(freshReadback.Artifact!, freshReadback.AcceptedCheckpoint,
            trial.Scenario.ExecutionAuthority, "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input,
            trial.Scenario.Plan, trial.Scenario.Plan.WorkItems[0].WorkItemKey, trial.Request,
            CampaignStateFactory.CreateInvocationTargetAllowance(restored.State, new(0)), allowance);
        var admitted = await CampaignCheckpointAcceptance.AcceptAsync(freshStore, transition);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, admitted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(admitted.AcceptedCheckpoint!, trial.Scenario.ExecutionAuthority,
            "style.synthetic", trial.Scenario.StyleProjection, trial.Scenario.Input, trial.Scenario.Plan, trial.Request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, freshStore, default, null, allowance.Clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        Assert.Equal(1, coordinator.Scope.RestoredRetryableProviderFailures);
        Assert.Equal(saved.PendingRetryAfterMilliseconds, coordinator.Scope.PendingRetryAfterMilliseconds);
        var resumed = trial with { Store = freshStore, Authority = authority, Coordinator = coordinator };
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(trial.Scenario))], usage: new(0, 0, 0, 0)))]);
        var next = resumed.Session(exchange);
        var result = await next.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(0, coordinator.Scope.PendingRetryAfterMilliseconds);
        Assert.Equal(1, result.RunEnvelope.RestoredRetryableProviderFailures);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, freshStore.Current.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(1, freshStore.Current.State.LineageCharges.OuterInvocations);
        Assert.Equal(1, freshStore.Dispatches);
        Assert.Equal(2, Assert.Single(next.Requests).AttemptNumber);
        Assert.Empty(next.State.CompletedToolExchanges);
        await resumed.CompleteAsync(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_HTTP_uses_the_product_request_commitment_profile_and_completion_authority(bool skip)
    {
        var trial = await SolusTrialAsync();
        var terminal = skip ? Encoding.UTF8.GetBytes("{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}")
            : C3ProposalTerminal(trial.Scenario);
        ScribeSessionTestDriver? session = null;
        var handler = new SolusHttpHandler(async (message, token) =>
        {
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("deepseek-v4-flash", wire.RootElement.GetProperty("model").GetString());
            Assert.Equal("enabled", wire.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", wire.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal(session!.Requests[0].OutputLimits.MaximumOutputTokens, wire.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(session.Requests[0].Messages.Select(message => message.Content),
                wire.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()));
            var active = Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation);
            Assert.Equal(CampaignProviderOperationKind.Dispatch, active.OperationKind);
            var commitment = SHA256.HashData(Encoding.UTF8.GetBytes("contract-scribe/physical-model-request/v1\n")
                .Concat(session.Requests[0].DeterministicUtf8).ToArray());
            Assert.Equal(Convert.ToHexString(commitment).ToLowerInvariant(), active.RequestCommitmentSha256);
            Assert.True(trial.Store.Reads >= trial.Store.Dispatches);
            return SolusTerminalHttp(terminal);
        });
        using var transport = SolusTransport(handler);
        session = trial.Session(transport);
        var result = await session.RunAsync();
        Assert.Equal(skip ? DocumentationScribeTerminalKind.Skip : DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(1, handler.Count);
        Assert.Equal(1, trial.Store.Dispatches);
        Assert.Equal(1, trial.Store.Settlements);
        Assert.Equal(CampaignProviderOperationKind.Host,
            Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).OperationKind);
        await trial.CompleteAsync(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_denied_admission_or_failed_reservation_has_zero_HTTP_effects(bool admission)
    {
        var trial = await SolusTrialAsync();
        trial.Store.DenyDispatch = !admission;
        using var cancellation = new CancellationTokenSource();
        if (admission) cancellation.Cancel();
        var handler = new SolusHttpHandler((_, _) => Task.FromResult(SolusTerminalHttp(C3ProposalTerminal(trial.Scenario))));
        using var transport = SolusTransport(handler);
        var session = trial.Session(transport);
        var result = await session.RunAsync(cancellation.Token);
        Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(0, handler.Count);
        Assert.Equal(0, trial.Coordinator.Scope.PhysicalProviderRequestCount);
        Assert.Equal(0, trial.Store.Settlements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_unconfirmed_checkpoint_readback_never_authorizes_more_effects(bool settlement)
    {
        var trial = await SolusTrialAsync();
        trial.Store.FailDispatchReadback = !settlement;
        trial.Store.FailSettlementReadback = settlement;
        var handler = new SolusHttpHandler((_, _) => Task.FromResult(SolusTerminalHttp(C3ProposalTerminal(trial.Scenario))));
        using var transport = SolusTransport(handler);
        var result = await trial.Session(transport).RunAsync();
        Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.True(trial.Coordinator.Scope.Failed);
        Assert.Equal(settlement ? 1 : 0, handler.Count);
        Assert.Equal(1, trial.Store.Dispatches);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Solus_runtime_cut_cannot_retire_before_real_product_settlement(bool failSettlement, bool durationCut)
    {
        var trial = await SolusTrialAsync(targetMilliseconds: durationCut ? 500 : 10_000, invocationMilliseconds: durationCut ? 500 : 10_000);
        trial.Store.HoldSettlement = true;
        trial.Store.FailSettlement = failSettlement;
        var handler = new SolusHttpHandler((_, _) => Task.FromResult(SolusTerminalHttp(C3ProposalTerminal(trial.Scenario))));
        using var transport = SolusTransport(handler);
        var session = trial.Session(transport);
        using var cancellation = new CancellationTokenSource();
        var running = session.RunAsync(cancellation.Token);
        await trial.Store.SettlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!durationCut) cancellation.Cancel();
        await session.RuntimeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SolusAgent.Api.Usage.DispatchExposure.Unknown,
            Assert.Single(session.RuntimeResult!.Outcome.Usage!.Attempts).Exposure);
        Assert.False(running.IsCompleted);
        Assert.False(session.ProviderCompletion.IsCompleted);
        Assert.Equal(CampaignProviderOperationKind.Dispatch,
            Assert.IsType<CampaignProviderReservation>(trial.Store.Current.State.ActiveReservation).OperationKind);
        trial.Store.ReleaseSettlement.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Equal(1, handler.Count);
        Assert.Equal(failSettlement, trial.Coordinator.Scope.Failed);
        Assert.Equal(failSettlement ? 0 : 1, trial.Store.Settlements);
        if (failSettlement)
        {
            var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(trial.Request, trial.Attempt, result);
            var registrar = Assert.IsType<CampaignProviderCompletionRegistrar>(trial.Authority.TryCreateCompletionRegistrar());
            Assert.False(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, outcome,
                trial.Coordinator.CurrentElapsed, out _));
        }
    }

    [Fact]
    public async Task Solus_runtime_preserves_partial_cache_and_reasoning_without_inventing_or_double_charging_usage()
    {
        var trial = await SolusTrialAsync();
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(trial.Scenario))],
                usage: new(inputTokens: 4, outputTokens: 3, cachedInputTokens: null, uncachedInputTokens: 1, reasoningTokens: 2)))]);
        var session = trial.Session(exchange);
        var result = await session.RunAsync();
        Assert.Equal(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
        Assert.Null(result.RunEnvelope.Usage!.CachedInputTokens);
        Assert.Equal(2, result.RunEnvelope.Usage.ReasoningTokens);
        var observed = Assert.Single(session.RuntimeResult!.Outcome.Usage!.Attempts).Usage;
        Assert.Null(Assert.Single(observed.ProviderCounters, counter => counter.Kind == SolusAgent.Api.Usage.ProviderTokenCounterKind.CacheRead).Value);
        Assert.Equal(2, Assert.Single(observed.ProviderCounters, counter => counter.Kind == SolusAgent.Api.Usage.ProviderTokenCounterKind.Reasoning).Value);
        Assert.Equal(3, trial.Store.Current.State.LineageCharges.OutputTokens.Observed);
        Assert.True(trial.Store.Current.State.LineageCharges.CachedInputTokens.ConservativeUnobserved > 0);
        await trial.CompleteAsync(result);
    }

    [Fact]
    public async Task Solus_runtime_candidate_callback_cut_waits_for_its_product_host_completion()
    {
        var trial = await SolusTrialAsync();
        trial.Store.HoldTerminalCompletion = true;
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(
            new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(trial.Scenario))], usage: new(0, 0, 0, 0)))]);
        var session = trial.Session(exchange);
        using var cancellation = new CancellationTokenSource();
        var running = session.RunAsync(cancellation.Token);
        await trial.Store.TerminalCompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await session.RuntimeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(running.IsCompleted);
        Assert.False(session.HostCompletion.IsCompleted);
        trial.Store.ReleaseTerminalCompletion.TrySetResult();
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, (await running).Terminal.Kind);
        Assert.Single(exchange.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solus_runtime_missing_usage_and_lost_HTTP_response_remain_conservative(bool lost)
    {
        var trial = await SolusTrialAsync(128);
        var handler = new SolusHttpHandler((_, _) => lost
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic response lost"))
            : Task.FromResult(SolusTerminalHttp(C3ProposalTerminal(trial.Scenario), usage: false)));
        using var transport = SolusTransport(handler);
        var session = trial.Session(transport);
        var result = await session.RunAsync();
        Assert.Equal(1, handler.Count);
        Assert.Equal(1, trial.Store.Settlements);
        Assert.True(trial.Store.Current.State.LineageCharges.OutputTokens.ConservativeUnobserved > 0);
        Assert.Equal(1, trial.Store.Current.State.LineageCharges.ProviderRequests.Observed);
        if (lost) Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
    }

    [Fact]
    public async Task Solus_runtime_invalid_terminal_and_exhausted_terminal_budget_never_trigger_repair()
    {
        foreach (var exhausted in new[] { false, true })
        {
            var trial = await SolusTrialAsync();
            var handler = new SolusHttpHandler((_, _) => Task.FromResult(SolusTerminalHttp(
                exhausted ? C3ProposalTerminal(trial.Scenario) : Encoding.UTF8.GetBytes("{\"kind\":\"proposal\"}"))));
            using var transport = SolusTransport(handler);
            if (exhausted)
                for (var index = 0; index < trial.Coordinator.Scope.Allowance.Limits.MaximumToolCalls; index++)
                    Assert.True(trial.Coordinator.Scope.Allowance.TryChargeTool());
            var session = trial.Session(transport);
            var result = await session.RunAsync();
            Assert.NotEqual(DocumentationScribeTerminalKind.Proposal, result.Terminal.Kind);
            Assert.True(handler.Count <= 1);
            Assert.Equal(exhausted ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Validation,
                Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
            if (exhausted) Assert.Equal(0, handler.Count);
            else Assert.NotNull(session.RuntimeResult);
            Assert.Equal(0, session.RuntimeResult?.RepairsAdmitted ?? 0);
        }
    }

    private static OpenAiCompatibleHttpModelExchange SolusTransport(HttpMessageHandler handler) => new(
        new(new Uri("https://example.test/v1/chat/completions"), "deepseek-v4-flash",
            new(OpenAiCompatibleThinkingMode.Enabled, OpenAiCompatibleReasoningEffort.High,
                OpenAiCompatibleToolChoice.Required, OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
                OpenAiCompatibleOutputTokenField.MaxTokens), networkEnabled: true), handler, disposeHandler: false);

    private static HttpResponseMessage SolusTerminalHttp(byte[] terminal, bool usage = true)
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "tool_calls",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "call.terminal",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "cs_terminal", ["arguments"] = Encoding.UTF8.GetString(terminal) },
                    }),
                },
            }),
        };
        if (usage) root["usage"] = new JsonObject
        {
            ["prompt_tokens"] = 2,
            ["completion_tokens"] = 1,
            ["prompt_cache_hit_tokens"] = 1,
            ["prompt_cache_miss_tokens"] = 1,
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public async Task Solus_runtime_duration_cut_preserves_shared_campaign_and_target_deadline_ownership(
        bool sharedDeadline, bool cancelled, bool hostCompletion)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: sharedDeadline ? 1500 : 500,
            invocationMilliseconds: 1000);
        var scope = trial.Coordinator.Scope;
        var preflight = Assert.IsType<DocumentationScribeHostPermit>(
            await scope.BeginHostAsync(DocumentationScribeHostOperation.Preflight, default));
        Assert.True(preflight.TryBeginOperation());
        clock.Advance(250);
        Assert.True(await scope.CompleteHostAsync(preflight, default));
        Assert.Equal(250, trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed);
        trial.Store.HoldTerminalCompletion = hostCompletion;
        trial.Store.HoldSettlement = !hostCompletion;
        var exchange = new ScriptedDocumentationScribeModelExchange([hostCompletion
            ? ScriptedDocumentationScribeStep.Return(new DocumentationScribeModelResponse([], [new(C3ProposalTerminal(trial.Scenario))], usage: new(0, 0, 0, 0)))
            : ScriptedDocumentationScribeStep.WaitForCancellation()]);
        var session = trial.Session(exchange);
        using var cancellation = new CancellationTokenSource();
        var running = session.RunAsync(cancellation.Token);
        if (hostCompletion) await trial.Store.TerminalCompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        else Assert.Single(exchange.Requests);
        Assert.Equal(sharedDeadline ? 750 : 500, session.State.RemainingMilliseconds);
        clock.Advance(sharedDeadline ? 750 : 500);
        if (!hostCompletion)
        {
            await trial.Store.SettlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
            if (cancelled) cancellation.Cancel();
            // The SDK delivers settlement with its separate closure grace after a provider cut.
            // Complete real C4 settlement before waiting for that acknowledgement to return.
            trial.Store.ReleaseSettlement.TrySetResult();
        }
        await session.RuntimeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SolusAgent.Api.Candidates.CandidateStopReason.DurationLimit, session.RuntimeResult!.StopReason);
        if (hostCompletion) Assert.False(running.IsCompleted);
        if (hostCompletion && cancelled) cancellation.Cancel();
        trial.Store.ReleaseTerminalCompletion.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelled) Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Terminal.Kind);
        else Assert.Equal(sharedDeadline ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Equal(1, trial.Store.Dispatches);
        Assert.Equal(1, trial.Store.Settlements);
        Assert.Single(exchange.Requests);
        await trial.CompleteAsync(result);
        Assert.Equal(cancelled ? CampaignWorkOutcomeCode.CancelledByCaller
            : sharedDeadline ? CampaignWorkOutcomeCode.BudgetExhausted : CampaignWorkOutcomeCode.Timeout,
            trial.Store.Current.State.WorkItems.Single(item => item.WorkItemKey == trial.Scenario.Plan.WorkItems[0].WorkItemKey).ClosedOutcome!.Code);
    }

    private static async Task<SolusTrial> SolusTrialAsync(int attempts = 3, TimeProvider? clock = null,
        int? targetMilliseconds = null, int? invocationMilliseconds = null)
    {
        var node = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        var limits = node["limits"]!.AsObject();
        limits["maximumAttempts"] = attempts;
        limits["maximumProviderRequests"] = 128;
        if (targetMilliseconds is { } target) limits["maximumElapsedMilliseconds"] = target;
        var template = DocumentationScribeValidation.ParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString())).Request!;
        var scenario = CreateProposalScenario(scribeRequestTemplate: template, targetLimit: 1,
            maximumAttemptsPerTarget: attempts, maximumProviderRequests: null, maximumInputTokens: null,
            maximumUncachedInputTokens: null, maximumOutputTokens: null, maximumCostMicrounits: null, maximumElapsedMilliseconds: null);
        var request = CreateScribeExchange(scenario.Plan.WorkItems[0], requestMutation: root => root["limits"] = limits.DeepClone(),
            resultMutation: root => root["runEnvelope"]!["elapsedMilliseconds"] = 0, scenario: scenario).Request;
        var initial = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var store = new SolusCheckpointStore(initial);
        var allowance = invocationMilliseconds is { } invocation
            ? new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
                maximumProviderRequests: request.Limits.MaximumProviderRequests,
                maximumToolCalls: request.Limits.MaximumToolCalls,
                maximumCachedInputTokens: request.Limits.MaximumInputTokens / 2,
                maximumUncachedInputTokens: Math.Min(request.Limits.MaximumUncachedInputTokens,
                    request.Limits.MaximumInputTokens - request.Limits.MaximumInputTokens / 2),
                maximumOutputTokens: request.Limits.MaximumOutputTokens,
                maximumElapsedMilliseconds: invocation,
                maximumRequestOutputTokens: request.Limits.MaximumOutputTokens,
                maximumRequestElapsedMilliseconds: Math.Min(invocation, request.Limits.MaximumElapsedMilliseconds)), clock)
            : CampaignInvocationTestPolicy.Create(request.Limits, clock);
        var admission = CampaignStateReducer.AdmitProviderInvocation(initial, scenario.ExecutionAuthority, "style.synthetic",
            scenario.StyleProjection, scenario.Input, scenario.Plan, scenario.Plan.WorkItems[0].WorkItemKey, request,
            CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(100)), allowance);
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, admission);
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
        var authority = CampaignStateReducer.CreateProviderInvocationAuthority(accepted.AcceptedCheckpoint!, scenario.ExecutionAuthority,
            "style.synthetic", scenario.StyleProjection, scenario.Input, scenario.Plan, request);
        var coordinator = new CampaignScribeExecutionCoordinator(authority, allowance, store, default, null, allowance.Clock.GetTimestamp());
        Assert.True(authority.TryBeginDispatch(out _));
        var attempt = Assert.IsType<CampaignProviderReservation>(accepted.Artifact!.State.ActiveReservation).AttemptId;
        return new(scenario, request, attempt, store, authority, coordinator);
    }

    private sealed record SolusTrial(ProposalScenario Scenario, DocumentationScribeRequest Request,
        DocumentationScribeAttemptId Attempt, SolusCheckpointStore Store, CampaignProviderInvocationAuthority Authority,
        CampaignScribeExecutionCoordinator Coordinator)
    {
        internal ScribeSessionTestDriver Session(IDocumentationScribeModelExchange exchange) => new(Request, Attempt,
            new(Request.ContextReferences.Select(reference => new DocumentationScribeContextContent(reference.ContextReferenceId,
                reference.Kind, reference.ContentSha256, reference.IncludedUtf8ByteCount, reference.IsTruncated,
                new string('c', reference.IncludedUtf8ByteCount))).ToImmutableArray(),
                Request.EvidenceReferences.Select(reference => new DocumentationScribeEvidenceContent(reference.EvidenceReferenceId,
                    reference.Authority, reference.ContentSha256, reference.IncludedUtf8ByteCount, reference.IsTruncated,
                    new string('e', reference.IncludedUtf8ByteCount))).ToImmutableArray()),
            new DocumentationScribeToolRegistryBuilder(Request.ToolPolicyId).Build(), exchange, Coordinator.Scope);

        internal async Task CompleteAsync(DocumentationScribeRunResult result)
        {
            var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(Request, Attempt, result);
            var registrar = Assert.IsType<CampaignProviderCompletionRegistrar>(Authority.TryCreateCompletionRegistrar());
            Assert.True(registrar.TryRegister(CampaignProviderCompletionKind.Ordinary, outcome, Coordinator.CurrentElapsed, out var completion));
            var transition = CampaignStateReducer.CompleteProviderInvocation(Authority.AcceptedCheckpoint.Artifact, completion!,
                Scenario.ExecutionAuthority, "style.synthetic", Scenario.StyleProjection, Scenario.Input, Scenario.Plan);
            Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
            var accepted = await CampaignCheckpointAcceptance.AcceptAsync(Store, transition);
            Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, accepted.Kind);
            Assert.Null(accepted.Artifact!.State.ActiveReservation);
        }
    }

    private sealed class SolusCheckpointStore(CampaignCheckpointArtifact initial) : ICampaignCheckpointStore
    {
        private readonly TransitionCheckpointStore inner = new(initial);
        internal CampaignCheckpointArtifact Current { get; private set; } = initial;
        internal int Reads { get; private set; }
        internal int Dispatches { get; private set; }
        internal int Settlements { get; private set; }
        internal bool DenyDispatch { get; set; }
        internal bool FailSettlement { get; set; }
        internal bool HoldSettlement { get; set; }
        internal bool FailDispatchReadback { get; set; }
        internal bool FailSettlementReadback { get; set; }
        internal bool HoldTerminalCompletion { get; set; }
        private bool failNextRead;
        internal Action? RetryReadback { get; set; }
        internal TaskCompletionSource SettlementEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSettlement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RetryWaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TerminalCompletionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseTerminalCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken token)
        {
            Reads++;
            if (Current.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.RetryWait }
                && RetryReadback is { } readback)
            {
                RetryReadback = null;
                readback();
            }
            if (failNextRead) { failNextRead = false; return ValueTask.FromResult(CampaignCheckpointReadResult.Unreadable()); }
            return inner.ReadAsync(token);
        }
        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token) =>
            inner.CreateIfAbsentAsync(bytes, revision, sha, token);
        public async ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(long expectedRevision, string expectedSha,
            ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token)
        {
            var next = CampaignStateJson.Parse(bytes).Artifact!;
            var dispatch = next.State.ActiveReservation is CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch };
            var settlement = Current.State.ActiveReservation is CampaignProviderReservation { OperationKind: CampaignProviderOperationKind.Dispatch } && !dispatch;
            if (HoldTerminalCompletion
                && Current.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.TerminalSubmission }
                && next.State.ActiveReservation is CampaignProviderReservation { HostPhase: not CampaignProviderHostPhase.TerminalSubmission })
            {
                TerminalCompletionEntered.TrySetResult();
                await ReleaseTerminalCompletion.Task;
            }
            if (dispatch && DenyDispatch) return new(CampaignCheckpointWriteKind.CurrentMismatch);
            if (settlement)
            {
                SettlementEntered.TrySetResult();
                if (HoldSettlement) await ReleaseSettlement.Task;
                if (FailSettlement) return new(CampaignCheckpointWriteKind.CurrentMismatch);
            }
            var result = await inner.ReplaceIfCurrentAsync(expectedRevision, expectedSha, bytes, revision, sha, token);
            if (result.Kind == CampaignCheckpointWriteKind.Written)
            {
                Current = next;
                if (dispatch) Dispatches++;
                if (settlement) Settlements++;
                failNextRead = dispatch && FailDispatchReadback || settlement && FailSettlementReadback;
                if (next.State.ActiveReservation is CampaignProviderReservation { HostPhase: CampaignProviderHostPhase.RetryWait })
                    RetryWaitEntered.TrySetResult();
            }
            return result;
        }
    }

    private sealed class SolusHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; return send(request, token); }
    }

    private sealed class SolusDeadlineClock : TimeProvider
    {
        private long timestamp;
        private readonly List<DeadlineTimer> timers = [];
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int milliseconds)
        {
            timestamp += milliseconds;
            foreach (var timer in timers.ToArray()) timer.Tick();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class DeadlineTimer(SolusDeadlineClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + (long)dueTime.TotalMilliseconds;
                return true;
            }
            internal void Tick()
            {
                if (disposed || owner.timestamp < due) return;
                due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
