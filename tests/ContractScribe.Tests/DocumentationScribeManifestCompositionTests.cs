using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(230)]
    public async Task Actual_builder_and_composition_expose_selected_members_without_request_owned_authorization(int propertyCount)
    {
        var source = "namespace EndToEnd; public class Fixture { public void Run() { } "
            + string.Join(" ", Enumerable.Range(0, propertyCount).Select(index => $"public int P{index:D3} {{ get; set; }}")) + " }";
        await using var fixture = await CompositionFixture.CreateBatchGroupingAsync(source);
        var campaign = fixture.CreateCampaign(includeAllOwners: true);
        var all = campaign.Plan.WorkItems.SelectMany(work => work.Targets).ToDictionary(target => target.SymbolRef);
        var selected = campaign.Plan.Batch.SelectedTargetKeys.Select(key => campaign.Plan.Batch.CompleteTargets
            .Single(target => target.TargetKey == key)).Select(target => all[target.SymbolRef]).ToImmutableArray();
        // The default quota includes class/property metadata; select the independently authorized supported method for this partial run.
        var method = all[fixture.Target.SymbolRef];
        Assert.Contains(selected, target => target.SymbolRef == method.SymbolRef);
        var config = CampaignConfiguration.Parse(File.ReadAllBytes(Path.Join(FindRepositoryRoot(), "tests", "fixtures", "campaign", "cli", "configuration-valid.json")));
        var policy = campaign.PlanningInput.ExecutionPolicy;
        var bundle = new ProductionRepositorySessionBundle(
            new(fixture.Root, fixture.Root, fixture.Session.InputIdentity, fixture.Session.InputIdentity, []),
            fixture.Policy, fixture.Session, fixture.Classified, fixture.Classified.Classification.ClassificationSet!,
            fixture.Observed, fixture.Extraction, fixture.AuditInputs, fixture.AuditDocument, [], HostToolchainFact.NotSelected, null);
        var built = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, [method.SymbolRef], policy, config, default);
        var request = DocumentationScribeValidation.ParseRequest(built.RequestUtf8Json).Request!;
        Assert.Equal(Math.Min(propertyCount + 2, 100), request.Manifest.Length);
        Assert.True(request.EvidenceReferences.Length <= 1);
        Assert.All(request.Manifest.Where(member => member.Target.SymbolRef != method.SymbolRef), member => Assert.Null(member.StyleProfile));
        Assert.True(built.Selection.Matches(request));
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(new([], [new(
            """{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray())], usage: new(0, 0, 0, 0)))]);
        var outcome = await DocumentationScribeComposition.ExecuteAsync(built.Selection, built.RequestUtf8Json,
            fixture.AttemptId, null, RuntimeOptions(), exchange);
        Assert.Equal(DocumentationScribeTerminalKind.Skip, outcome.RunResult!.Terminal.Kind);
        var startup = Assert.Single(exchange.Requests);
        using var targetData = JsonDocument.Parse(startup.Messages.Single(message => message.Kind == DocumentationScribeMessageKind.TargetEvidence).Content);
        Assert.Equal(request.Manifest.Length, targetData.RootElement.GetProperty("manifest").GetArrayLength());
        Assert.True(startup.DeterministicUtf8.Length < 1_048_576);
        Assert.Equal(1, outcome.RunResult.RunEnvelope.ProviderRequestCount);
        var repeated = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, [method.SymbolRef], policy, config, default);
        Assert.Equal(built.RequestUtf8Json.ToArray(), repeated.RequestUtf8Json.ToArray());
        if (propertyCount == 10)
        {
            var singleton = CampaignScribeRequestBuilder.BuildManifest(bundle, [method], [method.SymbolRef], policy, config, default);
            var singleExchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(new([], [new(
                """{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray())], usage: new(0, 0, 0, 0)))]);
            await DocumentationScribeComposition.ExecuteAsync(singleton.Selection, singleton.RequestUtf8Json,
                fixture.AttemptId, null, RuntimeOptions(), singleExchange);
            var singleStartup = Assert.Single(singleExchange.Requests);
            Assert.Equal(startup.Messages.Take(3).Select(message => message.Content), singleStartup.Messages.Take(3).Select(message => message.Content));
            Assert.NotEqual(string.Join("\n", startup.Tools.Select(tool => tool.InputSchemaJson)),
                string.Join("\n", singleStartup.Tools.Select(tool => tool.InputSchemaJson)));
            foreach (var tool in startup.Tools)
            {
                using var schema = JsonDocument.Parse(tool.InputSchemaJson);
                var selector = schema.RootElement.GetProperty("properties").TryGetProperty("targetId", out var semantic)
                    ? semantic : schema.RootElement.GetProperty("properties").GetProperty("scopeId");
                Assert.All(selector.GetProperty("enum").EnumerateArray(), id =>
                    Assert.True(request.Manifest.Any(member => member.TargetId == id.GetString())
                        || request.ContextReferences.Any(reference => reference.ContextReferenceId == id.GetString())
                        || request.EvidenceReferences.Any(reference => reference.EvidenceReferenceId == id.GetString())));
            }
        }


        // A valid same-snapshot sibling is still outside Host[A], even when the raw request authorizes it.
        var narrow = DocumentationScribeManifestSelection.CreateSingleton(fixture.AuditSelection, fixture.Request.ExecutionStyleProfile);
        var deniedExchange = new CountingExchange();
        var denied = await DocumentationScribeComposition.ExecuteAsync(narrow, built.RequestUtf8Json,
            fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
        Assert.Equal("scribe.preflight.authority-mismatch", denied.Code);
        Assert.Equal(0, deniedExchange.RequestCount);

        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["manifest"]!.AsArray().RemoveAt(0),
            root => { var first = root["manifest"]![0]!.DeepClone(); root["manifest"]!.AsArray().RemoveAt(0); root["manifest"]!.AsArray().Add(first); },
            root => root["manifest"]![0]!["auditOutcome"] = "audit.outcome.compliant",
            root => root["manifest"]![0]!["target"]!["sourceCommitment"]!["contentSha256"] = new string('0', 64),
            root => root["manifest"]!.AsArray().Single(member => member!["styleProfile"] is not null)!["styleProfile"]!["summary"]!["maximumScalars"] = 401,
        })
        {
            var changed = JsonNode.Parse(built.RequestUtf8Json.Span)!.AsObject();
            mutation(changed);
            var rejected = await DocumentationScribeComposition.ExecuteAsync(built.Selection, Encoding.UTF8.GetBytes(changed.ToJsonString()),
                fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
            Assert.Null(rejected.RunResult);
            Assert.Equal(0, deniedExchange.RequestCount);
        }
        if (propertyCount == 230)
        {
            var unselected = all.Values.First(target => selected.All(member => member.SymbolRef != target.SymbolRef));
            Assert.DoesNotContain(request.Manifest, member => member.Target.SymbolRef == unselected.SymbolRef);
            var widened = JsonNode.Parse(built.RequestUtf8Json.Span)!.AsObject();
            widened["executableTargetIds"]!.AsArray().Add("target.unselected-chunk-member");
            var rejected = await DocumentationScribeComposition.ExecuteAsync(built.Selection, Encoding.UTF8.GetBytes(widened.ToJsonString()),
                fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
            Assert.Null(rejected.RunResult);
            Assert.Equal(0, deniedExchange.RequestCount);
        }
    }
}
