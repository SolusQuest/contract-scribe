using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeInvocationBudgetTests
{
    [Fact]
    public void Default_action_and_request_policies_have_distinct_finite_bounds()
    {
        var policy = DocumentationScribeInvocationLimits.Create();
        Assert.Equal(64, policy.MaximumProviderRequests);
        Assert.Equal(512, policy.MaximumToolCalls);
        Assert.Equal(38_000_000, policy.MaximumCachedInputTokens);
        Assert.Equal(2_000_000, policy.MaximumUncachedInputTokens);
        Assert.Equal(900_000, policy.MaximumElapsedMilliseconds);
        Assert.Equal(300_000, policy.MaximumRequestElapsedMilliseconds);
        Assert.Equal(15_000, policy.MaximumConnectMilliseconds);
        Assert.Equal(65_536, policy.MaximumRequestOutputTokens);
        Assert.Equal(16, policy.MaximumToolCallsPerResponse);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentationScribeInvocationLimits.Create(maximumProviderRequests: 129));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentationScribeInvocationLimits.Create(maximumCachedInputTokens: 134_217_727, maximumUncachedInputTokens: 1));
    }

    [Fact]
    public void Reservation_does_not_count_a_physical_request_and_not_sent_does_not_charge_usage()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(allowance.TryReserveProvider(out var permit));
        Assert.Equal(0, allowance.ProviderRequestCount);
        Assert.False(allowance.TryChargeTool());
        Assert.True(allowance.SettleProvider(permit!, null));
        Assert.Equal(0, allowance.ProviderRequestCount);
        Assert.True(allowance.CanBeginProviderWork());
        Assert.False(allowance.SettleProvider(permit!, null));
        Assert.False(permit!.TryBeginDispatch());
    }

    [Fact]
    public void Two_target_scopes_use_the_same_request_and_tool_counters()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(maximumProviderRequests: 2, maximumToolCalls: 3));
        for (var target = 0; target < 2; target++)
        {
            Assert.True(allowance.TryChargeTool());
            Assert.True(allowance.TryReserveProvider(out var permit));
            Assert.True(permit!.TryBeginDispatch());
            Assert.False(permit.TryBeginDispatch());
            Assert.True(allowance.SettleProvider(permit, new(0, 0, 0, 0, 0)));
        }
        Assert.Equal(2, allowance.ProviderRequestCount);
        Assert.Equal(2, allowance.ToolCallCount);
        // Useful processing of the last accepted response can consume the remaining tool slot.
        Assert.True(allowance.TryChargeTool());
        Assert.False(allowance.TryReserveProvider(out _));
        Assert.Equal(2, allowance.ProviderRequestCount);
    }

    [Fact]
    public void Missing_partition_is_charged_independently_and_blocks_the_next_send()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
            maximumCachedInputTokens: 100, maximumUncachedInputTokens: 10, maximumOutputTokens: 100));
        Assert.True(allowance.TryReserveProvider(out var permit));
        Assert.True(permit!.TryBeginDispatch());
        Assert.True(allowance.SettleProvider(permit, new(InputTokens: 5, UncachedInputTokens: 5, OutputTokens: 1)));
        Assert.False(allowance.CanBeginProviderWork());
        Assert.Equal(DocumentationScribeInvocationStopReason.CachedInputTokens, allowance.StopReason);
        Assert.False(allowance.TryChargeTool());
    }

    [Fact]
    public void Complete_partitions_release_exposure_and_reasoning_does_not_double_output()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(
            maximumCachedInputTokens: 100, maximumUncachedInputTokens: 10, maximumOutputTokens: 20));
        Assert.True(allowance.TryReserveProvider(out var first)); Assert.True(first!.TryBeginDispatch());
        Assert.True(allowance.SettleProvider(first, new(6, 5, 1, 10, 9)));
        Assert.Equal(10, allowance.RemainingOutputTokens);
        Assert.True(allowance.TryReserveProvider(out var second)); Assert.True(second!.TryBeginDispatch());
        Assert.True(allowance.SettleProvider(second, new(2, 1, 1, 9, 9)));
        Assert.Equal(1, allowance.RemainingOutputTokens);
        Assert.True(allowance.CanBeginProviderWork());
    }

    [Fact]
    public void Known_overrun_is_retained_and_never_grants_another_tool_or_send()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(maximumOutputTokens: 10));
        Assert.True(allowance.TryReserveProvider(out var permit)); Assert.True(permit!.TryBeginDispatch());
        Assert.True(allowance.SettleProvider(permit, new(0, 0, 0, 11)));
        Assert.Equal(DocumentationScribeInvocationStopReason.OutputTokens, allowance.StopReason);
        Assert.False(allowance.TryChargeTool()); Assert.False(allowance.TryReserveProvider(out _));
    }

    [Fact]
    public void Inconsistent_complete_usage_does_not_consume_the_settlement_permit()
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(allowance.TryReserveProvider(out var permit)); Assert.True(permit!.TryBeginDispatch());
        Assert.False(allowance.SettleProvider(permit, new(4, 2, 3, 1)));
        Assert.True(allowance.SettleProvider(permit, new(5, 2, 3, 1)));
    }

    [Fact]
    public void A_foreign_allowance_cannot_start_or_settle_the_current_permit()
    {
        var first = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        var second = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(first.TryReserveProvider(out var permit));
        Assert.False(second.SettleProvider(permit!, null));
        Assert.True(permit!.TryBeginDispatch()); Assert.True(first.SettleProvider(permit, new(0, 0, 0, 0)));
    }

    [Fact]
    public void Request_deadline_includes_reservation_time_and_never_renews_the_action_clock()
    {
        var clock = new InvocationClock();
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(), clock);
        Assert.True(allowance.TryReserveProvider(out var first));
        clock.Advance(100_000); Assert.Equal(200_000, first!.RemainingMilliseconds);
        Assert.True(first.TryBeginDispatch()); Assert.True(allowance.SettleProvider(first, new(0, 0, 0, 0)));
        clock.Advance(650_000); Assert.True(allowance.TryReserveProvider(out var last));
        Assert.Equal(150_000, last!.Exposure.ElapsedMilliseconds);
        Assert.Equal(DocumentationScribeDeadlineOwner.Invocation, last.DeadlineOwner);
        clock.Advance(150_000); Assert.False(last.TryBeginDispatch());
        Assert.True(allowance.SettleProvider(last, null)); Assert.False(allowance.CanBeginProviderWork());
        Assert.Equal(900_000, allowance.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(0, null, 1)]
    [InlineData(1, 2, null)]
    [InlineData(1, null, 2)]
    public void A_known_input_partition_cannot_exceed_a_known_total(int total, int? cached, int? uncached)
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(allowance.TryReserveProvider(out var permit)); Assert.True(permit!.TryBeginDispatch());
        Assert.False(allowance.SettleProvider(permit, new(total, cached, uncached, 0)));
        Assert.True(allowance.SettleProvider(permit, new(0, 0, 0, 0)));
    }

    [Theory]
    [InlineData(300_000)]
    [InlineData(299_999)]
    public void A_finite_lifetime_owns_an_equal_or_shorter_request_deadline(int lifetime)
    {
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create());
        Assert.True(allowance.TryReserveProvider(out var permit));
        allowance.ShortenForLifetime(permit!, lifetime);
        Assert.Equal(lifetime, permit!.Exposure.ElapsedMilliseconds);
        Assert.Equal(DocumentationScribeDeadlineOwner.Lifetime, permit.DeadlineOwner);
        Assert.True(allowance.SettleProvider(permit, null));
    }

    [Fact]
    public async Task Host_deadline_ownership_distinguishes_action_from_operation_safety()
    {
        var clock = new InvocationClock();
        var allowance = new DocumentationScribeInvocationAllowance(DocumentationScribeInvocationLimits.Create(), clock);
        var scope = new DocumentationScribeExecutionScope(allowance, null, 0, 0);
        var ordinary = await scope.BeginHostAsync(DocumentationScribeHostOperation.RepositoryTool, default);
        Assert.NotNull(ordinary); Assert.True(ordinary.TryBeginOperation());
        Assert.Equal(DocumentationScribeDeadlineOwner.HostOperation, ordinary.DeadlineOwner);
        clock.Advance(30_000); scope.ObserveDeadline(ordinary);
        Assert.False(scope.LifetimeDeadlineReached); Assert.False(allowance.HasCheckedStop);
        Assert.True(await scope.CompleteHostAsync(ordinary, default));
        clock.Advance(850_000);
        var action = await scope.BeginHostAsync(DocumentationScribeHostOperation.RepositoryTool, default);
        Assert.NotNull(action); Assert.True(action.TryBeginOperation());
        Assert.Equal(20_000, action.MaximumMilliseconds);
        Assert.Equal(DocumentationScribeDeadlineOwner.Invocation, action.DeadlineOwner);
        clock.Advance(20_000); scope.ObserveDeadline(action);
        Assert.True(allowance.HasCheckedStop); Assert.Equal(DocumentationScribeInvocationStopReason.Elapsed, allowance.StopReason);
        Assert.True(await scope.CompleteHostAsync(action, default));
        Assert.Null(await scope.BeginHostAsync(DocumentationScribeHostOperation.RepositoryTool, default));
    }

    private sealed class InvocationClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int milliseconds) => timestamp += milliseconds;
    }
}
