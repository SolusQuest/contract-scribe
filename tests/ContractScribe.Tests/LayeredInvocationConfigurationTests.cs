using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed partial class LayeredConfigurationTests
{
    [Theory]
    [InlineData("maximumTargets")]
    [InlineData("maximumProviderRequests")]
    [InlineData("maximumToolCalls")]
    [InlineData("maximumCachedInputTokens")]
    [InlineData("maximumUncachedInputTokens")]
    [InlineData("maximumOutputTokens")]
    [InlineData("maximumElapsedMilliseconds")]
    [InlineData("maximumRequestOutputTokens")]
    [InlineData("maximumRequestElapsedMilliseconds")]
    [InlineData("maximumConnectMilliseconds")]
    [InlineData("maximumToolCallsPerResponse")]
    public void Invocation_policy_changes_spending_without_changing_correctness_or_provider_authority(string field)
    {
        var defaults = Defaults();
        var baseline = Resolve(defaults).Document;
        var changed = Resolve(defaults, Layer("budgets", new JsonObject
        {
            ["invocation"] = new JsonObject { [field] = 1 },
        })).Document;
        Assert.Equal(1, changed.ExactProjection.GetProperty("budgets").GetProperty("invocation").GetProperty(field).GetInt32());
        Assert.NotEqual(baseline.Budgets.Invocation, changed.Budgets.Invocation);
        var before = baseline.CreateExecutionPolicy();
        var after = changed.CreateExecutionPolicy();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(baseline.CreateExecutionCapability(before).PersistedProjection,
            changed.CreateExecutionCapability(after).PersistedProjection);
        var invalid = Layer("budgets", new JsonObject
        {
            ["invocation"] = new JsonObject { [field] = -1 },
        });
        Assert.Throws<CampaignConfigurationException>(() => Resolve(defaults, invalid));
    }
}
