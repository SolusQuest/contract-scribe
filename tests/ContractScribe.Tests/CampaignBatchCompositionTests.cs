using System.Collections.Immutable;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeCompositionTests
{
    [Fact]
    public async Task Real_Roslyn_type_with_ten_properties_counts_eleven_targets_and_preserves_unsupported_executor_progress()
    {
        var source = "namespace EndToEnd; public class Widget { "
            + string.Join(" ", Enumerable.Range(0, 10).Select(index => $"public int P{index} {{ get; set; }}"))
            + " } public class Fixture { public void Run() { } }";
        await using var fixture = await CompositionFixture.CreateBatchGroupingAsync(source);
        var campaign = fixture.CreateCampaign(includeAllOwners: true);
        var projector = new DocumentationDeclarationAuthorityProjector();
        var owners = fixture.Classified.Classification.ClassificationSet!.Targets
            .Where(target => target.SupportStatus == SupportStatus.Supported)
            .Select(target => projector.Project(fixture.Observed, target,
                target.PrimaryKind == PrimarySymbolKind.Method ? fixture.Request.StyleProfile : null))
            .Where(projection => projection.IsSuccess)
            .Select(projection => projection.Authority! with
            {
                GroupingAuthority = projection.Authority!.GroupingAuthority! with
                {
                    InstructionsSha256 = CampaignPlanner.CreateInstructionStackCommitment(fixture.Request.ContextReferences),
                    StyleConfigurationSha256 = CampaignStateFactory.CreateStyleConfigurationAuthority("style.public-api.v1",
                        campaign.StyleProjection).ContentSha256,
                },
            }).Select(authority => new CampaignPlanningOwnerAuthority([authority])).ToImmutableArray();
        var input = campaign.PlanningInput with { OwnerAuthority = new(owners), TargetLimit = new(100) };
        var plan = CampaignPlanner.Plan(input);
        var widget = plan.Batch.CompleteTargets.Where(target => target.SymbolRef.DocumentationCommentId
            is "T:EndToEnd.Widget" || target.SymbolRef.DocumentationCommentId.StartsWith("P:EndToEnd.Widget.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, widget.Length);
        Assert.All(widget, target => { Assert.True(target.BatchEligible); Assert.False(target.Dispatchable); });
        Assert.Single(widget.Select(target => target.GroupIdentity).Distinct());
        var initial = CampaignStateFactory.CreateInitial("style.public-api.v1", campaign.StyleProjection,
            campaign.ExecutionCapability, fixture.Session.InputIdentity, input, plan);
        Assert.Equal(11, initial.TargetProgress.Count(item =>
            item.Kind == CampaignTargetProgressKind.UnsupportedCurrentExecutor && widget.Contains(item.Target)));
        Assert.Equal(initial.Batch.Identity, CampaignStateJson.Parse(CampaignStateJson.Write(initial)).Artifact!.State.Batch.Identity);
        // Components and property accessors cannot inflate the count of canonical targets.
        Assert.DoesNotContain(widget, target => target.SymbolRef.DocumentationCommentId.StartsWith("M:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_Roslyn_nested_types_and_overloads_have_semantic_roots_and_families()
    {
        await using var fixture = await CompositionFixture.CreateBatchGroupingAsync("""
            namespace EndToEnd;
            public partial class Fixture {
                public void Run() { }
                public void Run(int value) { }
                public class Nested { public void Run() { } }
            }
            public partial class Fixture { public void Stop() { } }
            """);
        var projector = new DocumentationDeclarationAuthorityProjector();
        var projected = fixture.Classified.Classification.ClassificationSet!.Targets
            .Select(target => projector.Project(fixture.Observed, target, null))
            .Where(projection => projection.IsSuccess).Select(projection => projection.Authority!).ToArray();
        var methods = projected.Where(authority => authority.Target.PrimaryKind == PrimarySymbolKind.Method).ToArray();
        var root = methods.Single(authority => authority.Target.SymbolRef.DocumentationCommentId == "M:EndToEnd.Fixture.Run");
        var overload = methods.Single(authority => authority.Target.SymbolRef.DocumentationCommentId == "M:EndToEnd.Fixture.Run(System.Int32)");
        var nested = methods.Single(authority => authority.Target.SymbolRef.DocumentationCommentId == "M:EndToEnd.Fixture.Nested.Run");
        Assert.Equal("T:EndToEnd.Fixture", root.GroupingAuthority!.ContainingTypeDocumentationCommentId);
        Assert.Equal(root.GroupingAuthority.MemberFamilySha256, overload.GroupingAuthority!.MemberFamilySha256);
        Assert.Equal("T:EndToEnd.Fixture.Nested", nested.GroupingAuthority!.ContainingTypeDocumentationCommentId);
        Assert.NotEqual(root.GroupingAuthority.ContainingTypeDocumentationCommentId, nested.GroupingAuthority.ContainingTypeDocumentationCommentId);
        var stop = methods.Single(authority => authority.Target.SymbolRef.DocumentationCommentId == "M:EndToEnd.Fixture.Stop");
        Assert.Equal(root.GroupingAuthority.ContainingTypeDocumentationCommentId, stop.GroupingAuthority!.ContainingTypeDocumentationCommentId);
        var partialType = fixture.Classified.Classification.ClassificationSet!.Targets.Single(target =>
            target.SymbolRef.DocumentationCommentId == "T:EndToEnd.Fixture");
        Assert.False(projector.Project(fixture.Observed, partialType, null).IsSuccess);
    }

    [Fact]
    public async Task Actual_proposal_executor_obeys_zero_new_target_allowance_without_credentials_or_dispatch()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var initial = CampaignStateJson.CreateArtifact(campaign.InitialState);
        var store = new MemoryCampaignStore(initial);
        var exchange = new CountingExchange();
        var credentials = 0;
        var input = campaign.Input(fixture, store, exchange, RuntimeOptions()) with
        {
            TargetAllowance = CampaignStateFactory.CreateInvocationTargetAllowance(initial.State, new(0)),
            DeferredExchange = () => { credentials++; return exchange; },
        };
        var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(input);
        Assert.Equal(DocumentationCampaignProposalOutcomeKind.TargetLimit, outcome.Kind);
        Assert.Equal(0, credentials);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Equal(0, store.SuccessfulReplaceCount);
        Assert.Equal(initial.Sha256, store.Current!.Sha256);
    }

    [Fact]
    public async Task Actual_executor_persists_attempt_suppression_across_invocations_and_canonical_restore()
    {
        await using var fixture = await CompositionFixture.CreateProposalStageAsync();
        var campaign = fixture.CreateCampaign();
        var store = new MemoryCampaignStore(CampaignStateJson.CreateArtifact(campaign.InitialState));
        var exchange = new RetryableBatchExchange();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            store = new MemoryCampaignStore(CampaignStateJson.Parse(store.Current!.ExactUtf8Json.AsMemory()).Artifact!);
            var allowance = CampaignStateFactory.CreateInvocationTargetAllowance(store.Current!.State, new(attempt == 0 ? 1 : 0));
            var outcome = await DocumentationCampaignProposalExecutor.ExecuteAsync(
                campaign.Input(fixture, store, exchange, RuntimeOptions()) with { TargetAllowance = allowance });
            Assert.NotEqual(DocumentationCampaignProposalOutcomeKind.HostContractError, outcome.Kind);
        }
        var final = store.Current!.State;
        var work = final.WorkItems.Single(item => item.OuterAttemptCount > 0);
        Assert.Equal(3, work.OuterAttemptCount);
        Assert.Equal(CampaignAttemptDisposition.SuppressedAtAttemptLimit, work.AttemptDisposition);
        Assert.Equal(CampaignTerminalReason.Unresolved, final.TerminalOutcome!.Reason);
        Assert.Contains(final.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Suppressed);
        var restored = CampaignStateJson.Parse(CampaignStateJson.Write(final)).Artifact!;
        Assert.Empty(CampaignStateFactory.CreateInvocationTargetAllowance(restored.State, new(4096)).WorkItemKeys);
        var newStore = new MemoryCampaignStore(restored);
        var requests = exchange.RequestCount;
        var credentials = 0;
        await DocumentationCampaignProposalExecutor.ExecuteAsync(campaign.Input(fixture, newStore, exchange, RuntimeOptions()) with
        {
            DeferredExchange = () => { credentials++; return exchange; },
        });
        Assert.Equal(0, credentials);
        Assert.Equal(requests, exchange.RequestCount);
        Assert.Equal(0, newStore.SuccessfulReplaceCount);
    }

    private sealed class RetryableBatchExchange : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }
        public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(new DocumentationScribeModelResponse([], [],
                new DocumentationScribeModelFailure(DocumentationScribeModelFailureCode.TransientUnavailable),
                usage: new DocumentationScribeModelUsage(0, 0, 0, 0)));
        }
    }
}
