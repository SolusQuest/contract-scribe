using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRepositoryToolTests
{
    [Theory]
    [InlineData(30000, false)]
    [InlineData(40000, true)]
    public async Task Repository_elapsed_configuration_reaches_the_inner_operation_without_a_cumulative_session_deadline(int maximum, bool succeeds)
    {
        using var fixture = RepositoryToolFixture.Create();
        var elapsed = 0;
        var bundle = fixture.Bundle(limits: DocumentationScribeRepositoryToolLimits.Create(maximumElapsedMilliseconds: maximum),
            elapsed: () => TimeSpan.FromMilliseconds(elapsed),
            checkpoint: point => { if (point == DocumentationScribeRepositoryToolCheckpoint.BeforeEvidencePublication) elapsed += 31000; });
        for (var index = 0; index < 2; index++)
        {
            var result = await bundle.ReadExcerpt.InvokeAsync(new("context.instructions", "docs/guide.md"), default);
            Assert.Equal(succeeds, result.Excerpt is not null);
            if (!succeeds) Assert.Same(DocumentationScribeToolOutcome.TimedOut, result.Outcome);
        }
    }
}

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(120000, 40000)]
    [InlineData(20000, 20000)]
    public async Task Repository_outer_permit_uses_configured_duration_clipped_to_the_original_Runtime(int target, int maximum)
    {
        var clock = new SolusDeadlineClock();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: target, invocationMilliseconds: 120000,
            hostTimeouts: DocumentationScribeHostTimeouts.Create(repositoryToolMilliseconds: 40000));
        trial.Coordinator.Scope.BindRuntimeWindow(trial.Request, clock, clock.GetTimestamp());
        var permit = await trial.Coordinator.Scope.BeginHostAsync(DocumentationScribeHostOperation.RepositoryTool, default);
        Assert.NotNull(permit);
        Assert.Equal(maximum, permit.MaximumMilliseconds);
        clock.Advance(31000);
        Assert.Equal(maximum > 31000, permit.TryBeginOperation());
        Assert.Equal(Math.Max(0, maximum - 31000), permit.RemainingMilliseconds);
        Assert.True(await trial.Coordinator.Scope.CompleteHostAsync(permit, default));
    }
}
