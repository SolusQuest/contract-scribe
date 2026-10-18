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
    [Fact]
    public async Task Prepared_patch_retains_instruction_freshness_and_exact_manifest_selection()
    {
        await using var fixture = await CompositionFixture.CreateAsync();
        var prepared = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        Assert.True(prepared.IsProposalReady);
        var instructionPath = Path.Join(fixture.Root, fixture.Request.ContextReferences
            .First(reference => reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction).Path);
        var original = File.ReadAllBytes(instructionPath);
        await File.AppendAllTextAsync(instructionPath, "\nChanged after preparation.\n", new UTF8Encoding(false));
        var rejected = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, prepared);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, rejected.Status);
        Assert.Null(rejected.PatchRequest);
        Assert.Null(rejected.AcceptedCandidate);
        await File.WriteAllBytesAsync(instructionPath, original);
        var fresh = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        Assert.True(fresh.IsProposalReady);
        var replacement = DocumentationScribeManifestSelection.CreateSingleton(fixture.AuditSelection, fixture.Request.ExecutionStyleProfile);
        Assert.Same(fixture.SelectedAudit.PrimaryAudit, replacement.PrimaryAudit);
        var substituted = DocumentationScribeComposition.ConsumePreparedOutcome(replacement, fresh);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, substituted.Status);
        Assert.Null(substituted.PatchRequest);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(230)]
    public async Task Actual_builder_and_composition_expose_selected_members_without_request_owned_authorization(int additionalMemberCount)
    {
        var propertyCount = Math.Min(additionalMemberCount, 80);
        var methodCount = additionalMemberCount - propertyCount;
        var typeDocumentation = additionalMemberCount > 10 ? "/// <summary>Represents the bounded fixture.</summary>\n" : string.Empty;
        var source = "namespace EndToEnd;\n" + typeDocumentation + "public class Fixture { public void Run() { } "
            + string.Join(" ", Enumerable.Range(0, propertyCount).Select(index => $"public int P{index:D3} {{ get; set; }}"))
            + " " + string.Join(" ", Enumerable.Range(0, methodCount).Select(index => $"public void M{index:D3}() {{ }}")) + " }";
        await using var fixture = await CompositionFixture.CreateBatchGroupingAsync(source);
        var campaign = fixture.CreateCampaign(includeAllOwners: true);
        Assert.Equal(additionalMemberCount + 2, fixture.Classified.Classification.ClassificationSet!.Targets.Length);
        Assert.Equal(additionalMemberCount + (additionalMemberCount > 10 ? 1 : 2), campaign.Plan.Batch.CompleteTargets.Length);
        if (additionalMemberCount > 10)
        {
            var audit = DocumentationScribeAuditAuthority.Create(fixture.Classified, fixture.Observed, fixture.Policy,
                fixture.AuditInputs, fixture.AuditDocument);
            var type = fixture.Classified.Classification.ClassificationSet!.Targets.Single(target => target.SymbolRef.DocumentationCommentId == "T:EndToEnd.Fixture");
            Assert.Equal(AuditOutcome.Compliant, audit.Select(type).Outcome);
            Assert.DoesNotContain(campaign.Plan.Batch.CompleteTargets, target => target.SymbolRef == type.SymbolRef);
            Assert.Equal(additionalMemberCount + 1, campaign.Plan.Batch.CompleteTargets.Count(target => target.BatchEligible));
        }
        var all = campaign.Plan.WorkItems.SelectMany(work => work.Targets).ToDictionary(target => target.SymbolRef);
        Assert.Equal(propertyCount, all.Values.Count(target => target.PrimaryKind == PrimarySymbolKind.Property));
        Assert.Equal(methodCount + 1, all.Values.Count(target => target.M3Eligible));
        var selected = campaign.Plan.Batch.SelectedTargetKeys.Select(key => campaign.Plan.Batch.CompleteTargets
            .Single(target => target.TargetKey == key)).Select(target => all[target.SymbolRef]).ToImmutableArray();
        // At most 80 properties cannot fill the actual default quota; every 100-member chunk has a supported method.
        // Choose from the planner's selected collection rather than assuming Run's position in canonical order.
        var method = selected.First(target => target.M3Eligible);
        var config = CampaignConfiguration.Parse(File.ReadAllBytes(Path.Join(FindRepositoryRoot(), "tests", "fixtures", "campaign", "cli", "configuration-valid.json")));
        var policy = campaign.PlanningInput.ExecutionPolicy;
        var bundle = new ProductionRepositorySessionBundle(
            new(fixture.Root, fixture.Root, fixture.Session.InputIdentity, fixture.Session.InputIdentity, []),
            fixture.Policy, fixture.Session, fixture.Classified, fixture.Classified.Classification.ClassificationSet!,
            fixture.Observed, fixture.Extraction, fixture.AuditInputs, fixture.AuditDocument, [], HostToolchainFact.NotSelected, null);
        var built = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, [method.SymbolRef], policy, config, default);
        var request = DocumentationScribeValidation.ParseRequest(built.RequestUtf8Json).Request!;
        Assert.Equal(Math.Min(additionalMemberCount + 2, 100), request.Manifest.Length);
        Assert.Equal(selected.Select(target => target.SymbolRef), request.Manifest.Select(member => member.Target.SymbolRef));
        Assert.Equal(method.SymbolRef, request.ExecutionTarget.Target.SymbolRef);
        Assert.Equal(request.ExecutionTarget.TargetId, Assert.Single(request.ExecutableTargetIds));
        Assert.True(request.EvidenceReferences.Length <= 1);
        Assert.All(request.Manifest.Where(member => !all[member.Target.SymbolRef].M3Eligible), member => Assert.Null(member.StyleProfile));
        Assert.True(built.Selection.Matches(request));
        var exchange = new ScriptedDocumentationScribeModelExchange([ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Submit(request,
            """{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray()))], usage: new(0, 0, 0, 0))),
            ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Finish(request))], usage: new(0, 0, 0, 0, 0)))]);
        var outcome = await DocumentationScribeComposition.ExecuteAsync(built.Selection, built.RequestUtf8Json,
            fixture.AttemptId, null, RuntimeOptions(), exchange);
        Assert.Equal(DocumentationScribeTerminalKind.Skip, Assert.Single(outcome.RunResult!.Submissions).Result.Kind);
        Assert.Equal(2, exchange.Requests.Length);
        var startup = exchange.Requests[0];
        using var targetData = JsonDocument.Parse(startup.Messages.Single(message => message.Kind == DocumentationScribeMessageKind.TargetEvidence).Content);
        Assert.Equal(request.Manifest.Length, targetData.RootElement.GetProperty("manifest").GetArrayLength());
        Assert.True(startup.DeterministicUtf8.Length < 1_048_576);
        Assert.Equal(2, outcome.RunResult.RunEnvelope.ProviderRequestCount);
        using var semanticSchema = JsonDocument.Parse(startup.Tools.Single(tool =>
            tool.OperationId == DocumentationScribeSemanticToolSelection.OperationId).InputSchemaJson);
        Assert.Equal(request.Manifest.Where(member => all[member.Target.SymbolRef].M3Eligible)
                .Select(member => member.TargetId).Order(StringComparer.Ordinal),
            semanticSchema.RootElement.GetProperty("properties").GetProperty("targetId").GetProperty("enum")
                .EnumerateArray().Select(id => id.GetString()));
        using var sourceSchema = JsonDocument.Parse(startup.Tools.Single(tool =>
            tool.OperationId == DocumentationScribeRepositoryToolOperationIds.ReadExcerpt).InputSchemaJson);
        var sourceScopes = sourceSchema.RootElement.GetProperty("properties").GetProperty("scopeId")
            .GetProperty("enum").EnumerateArray().Select(id => id.GetString()).ToArray();
        Assert.All(request.Manifest, member => Assert.Contains(member.TargetId, sourceScopes));
        var repeated = CampaignScribeRequestBuilder.BuildManifest(bundle, selected, [method.SymbolRef], policy, config, default);
        Assert.Equal(built.RequestUtf8Json.ToArray(), repeated.RequestUtf8Json.ToArray());
        if (additionalMemberCount == 10)
        {
            var semanticExchange = new ScriptedDocumentationScribeModelExchange([
                ScriptedDocumentationScribeStep.Return(SemanticCall(request.ExecutionTarget.TargetId)),
                ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Submit(request,
                    """{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray()))], usage: new(0, 0, 0, 0))),
                ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Finish(request))], usage: new(0, 0, 0, 0, 0))),
            ]);
            var semanticOutcome = await DocumentationScribeComposition.ExecuteAsync(built.Selection, built.RequestUtf8Json,
                fixture.AttemptId, null, RuntimeOptions(), semanticExchange);
            Assert.Equal(DocumentationScribeTerminalKind.Skip, Assert.Single(semanticOutcome.RunResult!.Submissions).Result.Kind);
            Assert.Equal(3, semanticExchange.Requests.Length);
            using var semanticResult = JsonDocument.Parse(Assert.Single(semanticExchange.Requests[1].CompletedToolExchanges).ResultUtf8Json);
            var declaration = semanticResult.RootElement.GetProperty("page").GetProperty("core")
                .GetProperty("declaration").GetProperty("fact");
            Assert.True(declaration.TryGetProperty("contentId", out _) || declaration.TryGetProperty("contentRef", out _));
            // Class/property source remains readable, but neither kind is a semantic capability.
            foreach (var unsupported in request.Manifest.Where(member => !all[member.Target.SymbolRef].M3Eligible)
                .GroupBy(member => all[member.Target.SymbolRef].PrimaryKind).Select(group => group.First()))
            {
                var forged = new ScriptedDocumentationScribeModelExchange([
                    ScriptedDocumentationScribeStep.Return(SemanticCall(unsupported.TargetId)),
                ]);
                var rejected = await DocumentationScribeComposition.ExecuteAsync(built.Selection, built.RequestUtf8Json,
                    fixture.AttemptId, null, RuntimeOptions(), forged);
                Assert.Equal(DocumentationScribeFailureCode.ToolProtocol,
                    Assert.IsType<DocumentationScribeFailureTerminal>(rejected.RunResult!.Stop).Code);
                Assert.Single(forged.Requests);
            }
            var singleton = CampaignScribeRequestBuilder.BuildManifest(bundle, [method], [method.SymbolRef], policy, config, default);
            var singletonRequest = DocumentationScribeValidation.ParseRequest(singleton.RequestUtf8Json).Request!;
            var singleExchange = new ScriptedDocumentationScribeModelExchange([
                ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Submit(singletonRequest,
                    """{"kind":"skip","reason":"scribe.skip.insufficient-evidence","evidenceReferenceIds":[]}"""u8.ToArray()))], usage: new(0, 0, 0, 0))),
                ScriptedDocumentationScribeStep.Return(new([], [new(ScribeResultFixture.Finish(singletonRequest))], usage: new(0, 0, 0, 0, 0))),
            ]);
            var singletonOutcome = await DocumentationScribeComposition.ExecuteAsync(singleton.Selection, singleton.RequestUtf8Json,
                fixture.AttemptId, null, RuntimeOptions(), singleExchange);
            Assert.IsType<DocumentationScribeSkipTerminal>(Assert.Single(singletonOutcome.RunResult!.Submissions).Result);
            Assert.IsType<DocumentationScribeFinishedTerminal>(singletonOutcome.RunResult.Stop);
            Assert.Equal(2, singleExchange.Requests.Length);
            var singleStartup = singleExchange.Requests[0];
            Assert.Equal(startup.Messages.Take(3).Select(message => message.Content), singleStartup.Messages.Take(3).Select(message => message.Content));
            Assert.NotEqual(string.Join("\n", startup.Tools.Select(tool => tool.InputSchemaJson)),
                string.Join("\n", singleStartup.Tools.Select(tool => tool.InputSchemaJson)));
            foreach (var schema in startup.Tools.Select(tool => JsonDocument.Parse(tool.InputSchemaJson)))
            {
                using (schema)
                {
                    var selector = schema.RootElement.GetProperty("properties").TryGetProperty("targetId", out var semantic)
                        ? semantic : schema.RootElement.GetProperty("properties").GetProperty("scopeId");
                    Assert.All(selector.GetProperty("enum").EnumerateArray(), id =>
                        Assert.True(request.Manifest.Any(member => member.TargetId == id.GetString())
                            || request.ContextReferences.Any(reference => reference.ContextReferenceId == id.GetString())
                            || request.EvidenceReferences.Any(reference => reference.EvidenceReferenceId == id.GetString())));
                }
            }
        }


        // A valid same-snapshot sibling is still outside Host[A], even when the raw request authorizes it.
        var narrow = DocumentationScribeManifestSelection.CreateSingleton(built.Selection.PrimaryAudit, request.ExecutionStyleProfile);
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
            root => root["manifest"]!.AsArray().Single(member => member!["targetId"]!.GetValue<string>() == request.ExecutionTarget.TargetId)!["styleProfile"]!["summary"]!["maximumScalars"] = 401,
        })
        {
            var changed = JsonNode.Parse(built.RequestUtf8Json.Span)!.AsObject();
            mutation(changed);
            var rejected = await DocumentationScribeComposition.ExecuteAsync(built.Selection, Encoding.UTF8.GetBytes(changed.ToJsonString()),
                fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
            Assert.Null(rejected.RunResult);
            Assert.Equal(0, deniedExchange.RequestCount);
        }
        if (additionalMemberCount == 230)
        {
            var deferred = campaign.Plan.Batch.CompleteTargets.First(target => target.BatchEligible
                && !campaign.Plan.Batch.SelectedTargetKeys.Contains(target.TargetKey, StringComparer.Ordinal));
            var unselected = all[deferred.SymbolRef];
            Assert.DoesNotContain(request.Manifest, member => member.Target.SymbolRef == unselected.SymbolRef);
            var enlarged = CampaignScribeRequestBuilder.BuildManifest(bundle, selected.Add(unselected), [method.SymbolRef], policy, config, default);
            var enlargedRequest = DocumentationScribeValidation.ParseRequest(enlarged.RequestUtf8Json).Request!;
            Assert.Contains(enlargedRequest.Manifest, member => member.Target.SymbolRef == unselected.SymbolRef);
            var deniedChunk = await DocumentationScribeComposition.ExecuteAsync(built.Selection, enlarged.RequestUtf8Json,
                fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
            Assert.Equal("scribe.preflight.authority-mismatch", deniedChunk.Code);
            Assert.Equal(0, deniedExchange.RequestCount);
            var widened = JsonNode.Parse(built.RequestUtf8Json.Span)!.AsObject();
            widened["executableTargetIds"]!.AsArray().Add("target.unselected-chunk-member");
            var rejected = await DocumentationScribeComposition.ExecuteAsync(built.Selection, Encoding.UTF8.GetBytes(widened.ToJsonString()),
                fixture.AttemptId, null, RuntimeOptions(), deniedExchange);
            Assert.Null(rejected.RunResult);
            Assert.Equal(0, deniedExchange.RequestCount);
        }

        static DocumentationScribeModelResponse SemanticCall(string targetId) => new(
            [new DocumentationScribeModelToolCall(0, "call.semantic", DocumentationScribeSemanticToolSelection.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(new { targetId, pageSize = 1 }))], [], usage: new(0, 0, 0, 0));
    }
}
