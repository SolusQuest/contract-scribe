using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class LayeredConfigurationTests
{
    [Theory]
    [InlineData("terminalSubmissionMilliseconds", 300000, 86399999)]
    [InlineData("repositoryToolMilliseconds", 30000, 86399999)]
    [InlineData("checkpointAcceptanceMilliseconds", 30000, 86399999)]
    [InlineData("gitHubRequestMilliseconds", 30000, 86399999)]
    [InlineData("gitHubConnectMilliseconds", 10000, 86399999)]
    [InlineData("publicationMilliseconds", 300000, 300000)]
    [InlineData("semanticAuthorizationMilliseconds", 30000, 30000)]
    [InlineData("semanticQueryMilliseconds", 10000, 10000)]
    [InlineData("semanticClosureMilliseconds", 30000, 30000)]
    [InlineData("settlementTailMilliseconds", 30000, 30000)]
    public void Operational_timeouts_merge_with_strict_finite_bounds_without_changing_correctness(
        string field, int expectedDefault, int maximum)
    {
        var defaults = Defaults();
        var original = Resolve(defaults).Document;
        Assert.Equal(expectedDefault, original.ExactProjection.GetProperty("timeouts").GetProperty(field).GetInt32());
        foreach (var value in new[] { 1, maximum })
        {
            var policy = new JsonObject { [field] = value };
            // Validate the final merged pair, including a lower request ceiling.
            if (field == "gitHubRequestMilliseconds") policy["gitHubConnectMilliseconds"] = 1;
            if (field == "gitHubConnectMilliseconds") policy["gitHubRequestMilliseconds"] = maximum;
            var changed = Resolve(defaults, Layer("timeouts", policy)).Document;
            Assert.Equal(value, changed.ExactProjection.GetProperty("timeouts").GetProperty(field).GetInt32());
            var before = original.CreateExecutionPolicy();
            var after = changed.CreateExecutionPolicy();
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            Assert.Equal(original.CreateExecutionCapability(before).PersistedProjection,
                changed.CreateExecutionCapability(after).PersistedProjection);
            Assert.Equal(original.Budgets, changed.Budgets);
        }
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(0), JsonValue.Create(-1),
            JsonValue.Create((long)maximum + 1), JsonValue.Create(2147483648L), JsonValue.Create("1000"), JsonValue.Create(1.5) })
            AssertInvalid(defaults, Layer("timeouts", new JsonObject { [field] = value }));
        AssertInvalid(Defaults(root => root["timeouts"]![field] = 0),
            Layer("timeouts", new JsonObject { [field] = expectedDefault }));
    }

    [Fact]
    public void Operational_timeout_precedence_validates_the_final_pair_and_rejects_shape_changes()
    {
        var defaults = Defaults();
        var lower = Layer("timeouts", new JsonObject { ["gitHubConnectMilliseconds"] = 45000 });
        AssertInvalid(defaults, lower);
        var higher = Layer("timeouts", new JsonObject { ["gitHubRequestMilliseconds"] = 60000 });
        var resolved = Resolve(defaults, lower, higher).Document;
        Assert.Equal(45000, resolved.Timeouts.GitHub.ConnectMilliseconds);
        Assert.Equal(60000, resolved.Timeouts.GitHub.RequestMilliseconds);
        Assert.Equal(300000, resolved.Timeouts.Host.TerminalSubmissionMilliseconds);
        Assert.Equal(CampaignSemanticDurations.Default, resolved.Timeouts.Host.Semantic);
        AssertInvalid(defaults, Layer("timeouts", null));
        AssertInvalid(defaults, Layer("timeouts", new JsonArray()));
        AssertInvalid(defaults, Layer("timeouts", new JsonObject { ["unknown"] = 1000 }));
        AssertInvalid(defaults, Write("duplicate-timeouts.json",
            "{\"consumerConfigurationVersion\":1,\"timeouts\":{\"publicationMilliseconds\":1,\"publicationMilliseconds\":2}}"));
    }
}
