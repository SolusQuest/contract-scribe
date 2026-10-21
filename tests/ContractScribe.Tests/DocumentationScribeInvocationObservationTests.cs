using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeInvocationObservationTests
{
    [Fact]
    public async Task Actual_sends_distinguish_turns_retries_partial_usage_and_reasoning_subset()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        var scope = new DocumentationScribeExecutionScope(allowance, null, 0, 0);
        var first = Guid.NewGuid();
        async Task Send(Guid logical, int attempt, DocumentationScribeDispatchUsage? usage)
        {
            scope.ObserveProviderAttempt(logical, attempt);
            var permit = Assert.IsType<DocumentationScribeInvocationProviderPermit>(
                await scope.ReserveProviderAsync(new(new string('a', 64), 1, scope.PhysicalProviderRequestCount + 1, 1), default));
            Assert.True(scope.BeginPhysicalDispatch(permit));
            Assert.True(await scope.SettleProviderAsync(permit, new(DocumentationScribeDispatchDisposition.Success, usage), default));
        }
        await Send(first, 1, new(null, 5, 1, 10, 9));
        await Send(first, 2, new(2, 1, 1, 3, null));
        // A subsequent normal tool turn has a distinct logical identity, not a retry.
        await Send(Guid.NewGuid(), 1, new(3, null, 1, 4, 2));
        var observed = allowance.Observe();
        Assert.Equal(3, observed.PhysicalProviderDispatches);
        Assert.Equal(2, observed.LogicalProviderCalls);
        Assert.Equal(1, observed.RetryDispatches);
        Assert.Equal(new(11, 0), observed.Input);
        Assert.Equal(new(6, 1), observed.CachedInput);
        Assert.Equal(new(3, 0), observed.UncachedInput);
        Assert.Equal(new(17, 0), observed.Output);
        Assert.Equal(new(11, 1), observed.Reasoning);
        Assert.True(observed.CachedInputCharge.ConservativeUnobserved > 0);
        Assert.Equal(0, observed.UnsettledDispatches);
        Assert.Equal(0, observed.ActiveSent);
    }

    [Fact]
    public void Reservation_is_zero_usage_but_an_unsettled_actual_send_is_unknown()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(allowance.TryReserveProvider(out var permit));
        var reserved = allowance.Observe();
        Assert.Equal(0, reserved.PhysicalProviderDispatches);
        Assert.Equal(1, reserved.ActiveReserved);
        Assert.Equal(new(0, 0), reserved.Input);
        Assert.True(permit!.TryBeginDispatch());
        var sent = allowance.Observe();
        Assert.Equal(1, sent.PhysicalProviderDispatches);
        Assert.Equal(1, sent.ActiveSent);
        Assert.Equal(new(null, 1), sent.Input);
        Assert.Null(sent.LogicalProviderCalls);
        Assert.Equal(1, sent.UncorrelatedDispatches);
        Assert.Equal(1, sent.UnsettledDispatches);
        Assert.True(allowance.SettleProvider(permit, null));
        var lost = allowance.Observe();
        Assert.Equal(new(null, 1), lost.Output);
        Assert.True(lost.OutputCharge.ConservativeUnobserved > 0);
        Assert.Equal(0, lost.UnsettledDispatches);
    }

    [Fact]
    public async Task Valid_returned_measurement_survives_failed_durable_settlement_without_granting_budget()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        var scope = new DocumentationScribeExecutionScope(allowance, new RejectSettlement(), 0, 0);
        var permit = Assert.IsType<DocumentationScribeInvocationProviderPermit>(
            await scope.ReserveProviderAsync(new(new string('a', 64), 1, 1, 1), default));
        Assert.True(scope.BeginPhysicalDispatch(permit));
        Assert.False(await scope.SettleProviderAsync(permit,
            new(DocumentationScribeDispatchDisposition.Success, new(7, 6, 1, 5, 2)), default));
        var snapshot = allowance.Observe();
        Assert.Equal(new(7, 0), snapshot.Input);
        Assert.Equal(new(5, 0), snapshot.Output);
        Assert.Equal(1, snapshot.UnsettledDispatches);
        Assert.Equal(1, snapshot.ActiveSent);
        Assert.Equal(0, snapshot.InputCharge.TotalCharged);
        Assert.False(allowance.TryChargeTool());
        Assert.False(allowance.TryReserveProvider(out _));
        Assert.True(scope.Failed);
    }

    private sealed class RejectSettlement : IDocumentationScribeExecutionOperations
    {
        public ValueTask<bool> ReserveProviderAsync(DocumentationScribeDispatchDescriptor descriptor,
            DocumentationScribeInvocationProviderPermit permit, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<bool> SettleProviderAsync(DocumentationScribeInvocationProviderPermit permit,
            DocumentationScribeDispatchSettlement settlement, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<DocumentationScribeHostAdmission?> BeginHostAsync(DocumentationScribeHostOperation operation,
            int milliseconds, CancellationToken token) => ValueTask.FromResult<DocumentationScribeHostAdmission?>(null);
        public ValueTask<bool> CompleteHostAsync(DocumentationScribeHostOperation operation,
            int milliseconds, CancellationToken token) => ValueTask.FromResult(false);
    }
}
