using System.Runtime.Versioning;
using ContractScribe.Cli;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Changed_operational_configuration_retires_the_original_semantic_exposure_in_a_fresh_process_without_provider_replay()
    {
        var vector = ReadBoundaryMatrix().Single(item => item.Hook == CampaignProcessBoundaryHooks.SemanticAuthorizationAfterReadback);
        await RunCrashVectorAsync(vector, changedOperationalTimeouts: true);
    }
}
