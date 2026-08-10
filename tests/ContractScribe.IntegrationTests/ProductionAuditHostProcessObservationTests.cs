using System.Text;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Host-wide process observation")]
public sealed class ProductionAuditHostProcessObservationTests
{
    [Fact]
    public async Task DefaultProductionHostCompositionPublishesTheRealProcessFact()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        Directory.CreateDirectory(Path.Join(fixture.Root, "TestResults"));
        var outcome = await new ProductionAuditHost(
                new HostBuildProvenance(new string('1', 40)))
            .RunAsync(
                new ProductionAuditRequest(
                    fixture.Root,
                    "App/App.csproj",
                    Encoding.UTF8.GetBytes(
                        "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n"),
                    ResolvedPublicationTarget.ForTestResult(fixture.Root)),
                new ProductionAuditHostControls());

        Assert.Equal(HostExecutionOutcome.Succeeded, outcome.Terminal.ExecutionOutcome);
        var processFact = Assert.Single(
            outcome.Terminal.MeasuredBounds,
            fact => fact.Name == "toolchain-subprocess-count");
        Assert.True(processFact.Measured >= 0);
        Assert.Equal(HostEnforcementClass.ObservableOnly, processFact.EnforcementClass);
    }
}

[CollectionDefinition("Host-wide process observation", DisableParallelization = true)]
public sealed class HostWideProcessObservationCollection;
