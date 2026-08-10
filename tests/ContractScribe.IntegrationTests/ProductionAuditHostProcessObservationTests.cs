namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Host-wide process observation")]
public sealed class ProductionAuditHostProcessObservationTests
{
    [Fact]
    public async Task ToolchainProcessMeter_ClassifiesTheSelectedProductionToolchain()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        using var meter = new ToolchainProcessMeter();
        var selected = await MsBuildBootstrap.EnsureRegisteredForProductionHostAsync(
            Path.Join(fixture.Root, "App"),
            CancellationToken.None);

        _ = meter.SelectToolchain(selected);
    }
}

[CollectionDefinition("Host-wide process observation", DisableParallelization = true)]
public sealed class HostWideProcessObservationCollection;
