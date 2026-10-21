using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_real_groups_progress_without_packing_or_starving_closed_targets(bool oversized)
    {
        await using var fixture = await Fixture.CreateAsync(providerScenario: "shared-closed");
        static string Type(string name, int count) =>
            "/// <summary>Provides synthetic grouped operations.</summary>\npublic static class " + name + "\n{\n"
            + string.Join("\n", Enumerable.Range(0, count).Select(index => $"    public static void M{index:D3}() {{ }}")) + "\n}\n";
        var declaredGroups = oversized ? new[] { (Name: "AFirst", Count: 230) }
            : new[] { (Name: "AFirst", Count: 95), (Name: "BSecond", Count: 11), (Name: "CThird", Count: 1) };
        await ApplyEvidenceSources(fixture, new Dictionary<string, string>
        {
            ["App/App.cs"] = "namespace Fixture;\n" + string.Concat(declaredGroups.Select(group => Type(group.Name, group.Count))),
        });
        await SetAutomaticQuota(fixture, 100);
        var sizes = oversized ? new[] { 100, 100, 30 } : new[] { 95, 12 };
        // Each batch and the final replay retain one authenticated history.
        fixture.GitHub.MaximumRequests = (sizes.Length + 1) * 20_000;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CampaignCheckpointArtifact? previous = null;
        foreach (var size in sizes)
        {
            var received = fixture.Provider.RequestCount;
            var result = await fixture.Run();
            AssertResult(result, 5, "host-failure");
            var current = fixture.Checkpoint();
            Assert.Equal(oversized ? 230 : 107, current.State.Batch.CompleteTargets.Length);
            Assert.Equal(size, current.State.Batch.SelectedTargetKeys.Length);
            // Complete-plan order may interleave types. The fixture declares the
            // whole AFirst group before BSecond and CThird; each retains its order.
            var complete = current.State.Batch.CompleteTargets;
            var grouped = declaredGroups.SelectMany(group => complete.Where(target => target.SymbolRef.DocumentationCommentId
                .StartsWith($"M:Fixture.{group.Name}.", StringComparison.Ordinal)));
            var expected = grouped.Where(target => !seen.Contains(target.TargetKey)).Take(size).Select(target => target.TargetKey);
            Assert.Equal(expected, current.State.Batch.SelectedTargetKeys);
            foreach (var key in current.State.Batch.SelectedTargetKeys) Assert.True(seen.Add(key));
            Assert.Equal(seen.Count, current.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
            Assert.Equal(current.State.Batch.CompleteTargets.Length - seen.Count,
                current.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
            Assert.Equal(CampaignTerminalReason.Unresolved, current.State.TerminalOutcome!.Reason);
            Assert.All(current.State.WorkItems.Where(work => work.Status == CampaignWorkStatus.Closed), work =>
                Assert.Equal(CampaignWorkOutcomeCode.InsufficientEvidence, work.ClosedOutcome!.Code));
            if (previous is not null)
            {
                Assert.Equal(previous.State.Snapshot, current.State.Snapshot);
                Assert.NotEqual(previous.State.Batch.Identity, current.State.Batch.Identity);
                Assert.Equal(previous.State.Batch.CompleteTargets.ToArray(), current.State.Batch.CompleteTargets.ToArray());
                foreach (var work in previous.State.WorkItems.Where(work => work.Status == CampaignWorkStatus.Closed))
                    Assert.Equal(work, current.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey));
            }
            else
            {
                foreach (var group in declaredGroups)
                {
                    var members = complete.Where(target => target.SymbolRef.DocumentationCommentId
                        .StartsWith($"M:Fixture.{group.Name}.", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(group.Count, members.Length);
                    Assert.Single(members.Select(target => target.GroupIdentity).Distinct());
                }
                Assert.Equal(declaredGroups.Length, complete.Select(target => target.GroupIdentity).Distinct().Count());
            }
            var usage = EvidenceUsage(result);
            Assert.Equal(fixture.Provider.RequestCount - received, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
            Assert.Equal(size, usage.GetProperty("work").GetProperty("skippedTargets").GetInt32());
            Assert.Equal(0, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
            AssertObservationPrivacy(fixture, result, current);
            previous = current;
        }
        var final = Assert.IsType<CampaignCheckpointArtifact>(previous);
        Assert.Equal(final.State.Batch.CompleteTargets.Length, seen.Count);
        var counts = GateCounts(fixture);
        AssertResult(await fixture.Run(), 5, "host-failure");
        var replay = GateCounts(fixture);
        // An unpublished unresolved campaign rechecks its correctness inputs;
        // only an authenticated publication lifecycle gate precedes discovery.
        Assert.Equal(counts.Discovery + 1, replay.Discovery);
        Assert.Equal(counts.Created, replay.Created);
        Assert.Equal(counts.Sent, replay.Sent);
        Assert.Equal(counts.Physical, replay.Physical);
        Assert.Equal(final.Sha256, fixture.Checkpoint().Sha256);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.InRange(fixture.GitHub.Requests.Count, 1, fixture.GitHub.MaximumRequests);
        await fixture.AssertSourceUnchanged();
    }

    private static async Task ApplyEvidenceSources(Fixture fixture, IReadOnlyDictionary<string, string> files)
    {
        foreach (var pair in files)
        {
            var path = Path.Join(fixture.Repository.Root, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, pair.Value, new UTF8Encoding(false, true));
            fixture.GovernedFiles[pair.Key] = (await File.ReadAllBytesAsync(path),
                OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path));
        }
        fixture.Source = fixture.GovernedFiles["App/App.cs"].Bytes;
        AdvanceTarget(fixture, fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes),
            "Install reviewed synthetic continuation evidence source\n");
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task Automatic_five_acknowledged_members_restore_under_changed_quota_without_admitting_a_sixth(int quota)
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 6, providerScenario: "shared");
        await SetAutomaticQuota(fixture, 5);
        var acknowledgement = Path.Join(fixture.Outside, "five-members.ack");
        var environment = fixture.Environment();
        foreach (var pair in CampaignCliProcessTests.HookEnvironment(CampaignProcessBoundaryHooks.ScribeProgressAfterReadback,
            acknowledgement, Path.Join(fixture.Outside, "never-release-five"))) environment[pair.Key] = pair.Value;
        CampaignCheckpointArtifact saved;
        using (var running = CampaignCliProcessTests.Start(fixture.Args(), environment))
        {
            try
            {
                await CampaignCliProcessTests.WaitForFileAsync(acknowledgement, running,
                    CampaignProcessBoundaryHooks.ScribeProgressAfterReadback, TimeSpan.FromMinutes(3));
                saved = fixture.Checkpoint();
                Assert.Equal(5, saved.State.ScribeProgress!.Members.Count(member => member.ContentSha256 is not null));
                Assert.Equal(5, saved.State.WorkItems.Count(work => work.Status == CampaignWorkStatus.ProposalComplete));
                Assert.DoesNotContain(saved.State.WorkItems, work => work.Status == CampaignWorkStatus.Accepted);
                Assert.Null(saved.State.CandidateObservation);
                Assert.Equal(5, saved.State.Batch.CreationQuota);
                Assert.Equal(5, saved.State.Batch.SelectedTargetKeys.Length);
                Assert.Single(saved.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred);
                Assert.All(saved.State.DispatchObservations, row => Assert.Equal(CampaignDispatchObservationState.Settled, row.State));
            }
            finally { await CampaignCliProcessTests.StopAsync(running); }
        }
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        await SetAutomaticQuota(fixture, quota);
        var received = fixture.Provider.RequestCount;
        var result = await fixture.Run();
        AssertResult(result, 0, "published");
        var current = fixture.Checkpoint();
        Assert.Equal(saved.State.Batch.Identity, current.State.Batch.Identity);
        Assert.Equal(saved.State.Batch.PlanCommitment, current.State.Batch.PlanCommitment);
        Assert.Equal(saved.State.Batch.CreationQuota, current.State.Batch.CreationQuota);
        Assert.Equal(saved.State.Batch.CompleteTargets.ToArray(), current.State.Batch.CompleteTargets.ToArray());
        Assert.Equal(saved.State.Batch.SelectedTargetKeys.ToArray(), current.State.Batch.SelectedTargetKeys.ToArray());
        Assert.Equal(5, current.State.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
        Assert.Equal(saved.State.Batch.SelectedTargetKeys.Order(StringComparer.Ordinal),
            current.State.Batch.CompleteTargets.Where(target => current.State.CandidateObservation!.AcceptedWorkItemKeys.Contains(target.WorkItemKey))
                .Select(target => target.TargetKey).Order(StringComparer.Ordinal));
        var deferred = Assert.Single(current.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred);
        Assert.Equal(0, current.State.WorkItems.Single(work => work.WorkItemKey == deferred.Target.WorkItemKey).OuterAttemptCount);
        foreach (var member in saved.State.ScribeProgress!.Members)
        {
            var restored = current.State.ScribeProgress!.Members.Single(item => item.WorkItemKey == member.WorkItemKey);
            Assert.Equal(member.ContentSha256, restored.ContentSha256);
            Assert.Equal(member.OuterAttemptOrdinal, restored.OuterAttemptOrdinal);
        }
        Assert.Equal(saved.State.LineageCharges.ProviderRequests, current.State.LineageCharges.ProviderRequests);
        Assert.Equal(saved.State.LineageCharges.InputTokens, current.State.LineageCharges.InputTokens);
        Assert.Equal(saved.State.LineageCharges.OutputTokens, current.State.LineageCharges.OutputTokens);
        Assert.Equal(received, fixture.Provider.RequestCount);
        var usage = EvidenceUsage(result);
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        Assert.Equal(0, usage.GetProperty("work").GetProperty("startedTargets").GetInt32());
        Assert.Equal(5, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
        // ProposalComplete receipts are restored; they were not already M2 Accepted.
        Assert.Equal(0, usage.GetProperty("work").GetProperty("reconstructedTargets").GetInt32());
        AssertObservationPrivacy(fixture, result, current);
        await ConfigureSealedResume(fixture);
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["provider"]!["model"] = "changed-after-sealing";
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());
        var counts = GateCounts(fixture);
        var writes = fixture.GitHub.Mutations;
        var head = fixture.GitHub.PullRequests[0]["head"]!.ToJsonString();
        var waited = await fixture.Run();
        AssertResult(waited, 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Equal(head, fixture.GitHub.PullRequests[0]["head"]!.ToJsonString());
        Assert.Equal(current.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertZeroInvocationWork(waited);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Automatic_reopened_PR_observation_precedes_terminal_history_and_all_new_work()
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        AssertResult(await fixture.Run(), 0, "published-partial");
        var original = fixture.Checkpoint();
        var head = fixture.GitHub.PullRequests[0]["head"]!.ToJsonString();
        fixture.GitHub.Lifecycle("closed");
        var closed = fixture.GitHub.PullRequests[0].DeepClone();
        var counts = GateCounts(fixture);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run(), 4, "closed-unmerged");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Equal(original.Sha256, fixture.Checkpoint().Sha256);
        fixture.GitHub.Lifecycle("ready");
        Assert.Equal("closed", closed["state"]!.GetValue<string>());
        Assert.Equal("open", fixture.GitHub.PullRequests[0]["state"]!.GetValue<string>());
        Assert.NotEqual(closed["closed_at"]!.ToJsonString(), fixture.GitHub.PullRequests[0]["closed_at"]?.ToJsonString());
        await ConfigureSealedResume(fixture);
        var reopened = await fixture.Run();
        AssertResult(reopened, 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Equal(original.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(head, fixture.GitHub.PullRequests[0]["head"]!.ToJsonString());
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertZeroInvocationWork(reopened);
        AssertObservationPrivacy(fixture, reopened, original);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_received_response_loss_and_returned_missing_usage_preserve_unknown_history(bool responseLost)
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true, includeUsage: responseLost);
        if (responseLost) fixture.Provider.DropResponseAt = 1;
        var first = await fixture.Run();
        AssertResult(first, responseLost ? 3 : 0, responseLost ? "conflict" : "published-partial");
        Assert.Equal(1, fixture.Provider.RequestCount);
        var saved = fixture.Checkpoint();
        var dispatch = Assert.Single(saved.State.DispatchObservations);
        Assert.Equal(CampaignDispatchObservationState.Settled, dispatch.State);
        Assert.True(saved.State.LineageCharges.InputTokens.ConservativeUnobserved > 0);
        var usage = EvidenceUsage(first);
        Assert.Equal(1, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("tokens").GetProperty("input").GetProperty("observed").ValueKind);
        Assert.Equal(1, usage.GetProperty("tokens").GetProperty("input").GetProperty("missingDispatches").GetInt32());
        Assert.Equal("unknown", usage.GetProperty("completeness").GetString());
        if (responseLost)
        {
            Assert.Null(saved.State.CandidateObservation);
            Assert.Empty(fixture.GitHub.PullRequests);
            fixture.Provider.DropResponseAt = null;
            await ConfigureSealedResume(fixture);
            var received = fixture.Provider.RequestCount;
            var fresh = await fixture.Run();
            AssertResult(fresh, 0, "published");
            var restored = fixture.Checkpoint();
            var retained = Assert.Single(restored.State.DispatchObservations,
                row => row.AttemptId == dispatch.AttemptId && row.DispatchOrdinal == dispatch.DispatchOrdinal);
            Assert.Equal(dispatch, retained);
            var freshCalls = fixture.Provider.RequestCount - received;
            Assert.True(freshCalls > 0);
            Assert.Equal(freshCalls, EvidenceUsage(fresh).GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
            Assert.Equal(saved.State.LineageCharges.ProviderRequests.TotalCharged + freshCalls,
                restored.State.LineageCharges.ProviderRequests.TotalCharged);
            Assert.Equal(saved.State.LineageCharges.InputTokens.ConservativeUnobserved,
                restored.State.LineageCharges.InputTokens.ConservativeUnobserved);
            AssertObservationPrivacy(fixture, fresh, restored);
        }
        else Assert.Single(saved.State.WorkItems, work => work.Status == CampaignWorkStatus.Accepted);
        var final = fixture.Checkpoint();
        var counts = GateCounts(fixture);
        var waited = await fixture.Run();
        AssertResult(waited, 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(final.Sha256, fixture.Checkpoint().Sha256);
        AssertZeroInvocationWork(waited);
        Assert.Equal(final.State.LineageCharges.InputTokens.ConservativeUnobserved,
            EvidenceUsage(waited).GetProperty("history").GetProperty("input").GetProperty("conservativeUnobserved").GetInt64());
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        AssertObservationPrivacy(fixture, first, saved);
        await fixture.AssertSourceUnchanged();
    }

    private static void AssertZeroInvocationWork(CampaignCliProcessTests.ProcessResult result)
    {
        var usage = EvidenceUsage(result);
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
        foreach (var name in new[] { "startedTargets", "resumedTargets", "acceptedTargets", "reconstructedTargets" })
            Assert.Equal(0, usage.GetProperty("work").GetProperty(name).GetInt32());
        foreach (var token in new[] { "input", "cachedInput", "uncachedInput", "output", "reasoning" })
        {
            Assert.Equal(0, usage.GetProperty("tokens").GetProperty(token).GetProperty("observed").GetInt64());
            Assert.Equal(0, usage.GetProperty("tokens").GetProperty(token).GetProperty("missingDispatches").GetInt32());
        }
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Automatic_observation_corpus_preserves_real_partial_nested_property_and_instruction_scopes()
    {
        await using var fixture = await Fixture.CreateAsync(providerScenario: "shared");
        var corpus = Path.Join(CampaignCliProcessTests.RepositoryRoot, "tests", "fixtures", "campaign", "m7-observation");
        var sources = new Dictionary<string, string>();
        foreach (var relative in new[] { "App.cs", "Other.cs", "Scoped/Scoped.cs", "AGENTS.md", "Scoped/AGENTS.md" })
            sources[relative == "AGENTS.md" ? relative : "App/" + relative] = await File.ReadAllTextAsync(Path.Join(corpus, relative));
        await ApplyEvidenceSources(fixture, sources);
        var result = await fixture.Run();
        AssertResult(result, 0, "published-partial");
        var checkpoint = fixture.Checkpoint();
        var targets = checkpoint.State.Batch.CompleteTargets;
        var ids = targets.Select(target => target.SymbolRef.DocumentationCommentId).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "M:Fixture.App.A", "M:Fixture.App.A(System.Int32)", "M:Fixture.App.B", "M:Fixture.App.B(System.Int32)",
            "M:Fixture.App.Nested.Run", "M:Fixture.App.Nested.Run(System.Int32)", "M:Fixture.Scoped.Operations.Run",
            "P:Fixture.Metadata.Value", "T:Fixture.Metadata",
        }, ids);
        Assert.Equal(9, checkpoint.State.Batch.SelectedTargetKeys.Length);
        Assert.Equal(4, checkpoint.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.AcceptedProposal));
        Assert.Equal(3, checkpoint.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(2, checkpoint.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.UnsupportedCurrentExecutor));
        CampaignBatchTarget Target(string id) => targets.Single(target => target.SymbolRef.DocumentationCommentId == id);
        var a = Target("M:Fixture.App.A");
        var aOverload = Target("M:Fixture.App.A(System.Int32)");
        var b = Target("M:Fixture.App.B");
        var nested = Target("M:Fixture.App.Nested.Run");
        var scoped = Target("M:Fixture.Scoped.Operations.Run");
        Assert.Equal(a.GroupIdentity, aOverload.GroupIdentity);
        Assert.Equal(a.GroupIdentity, b.GroupIdentity);
        Assert.Equal(a.MemberFamilyIdentity, aOverload.MemberFamilyIdentity);
        Assert.NotEqual(a.MemberFamilyIdentity, b.MemberFamilyIdentity);
        Assert.NotEqual(a.GroupIdentity, nested.GroupIdentity);
        Assert.NotEqual(a.GroupIdentity, scoped.GroupIdentity);
        using var wire = JsonDocument.Parse(fixture.Provider.RequestBodies[0]);
        var messages = wire.RootElement.GetProperty("messages");
        using var facts = JsonDocument.Parse(messages[4].GetProperty("content").GetString()!);
        var manifest = facts.RootElement.GetProperty("manifest").EnumerateArray().ToArray();
        Assert.Equal(ids, manifest.Select(member => member.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString())
            .Order(StringComparer.Ordinal));
        JsonElement Member(string id) => manifest.Single(member => member.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString() == id);
        var rootReferences = Member("M:Fixture.App.A").GetProperty("contextReferenceIds").EnumerateArray().Select(id => id.GetString()).ToArray();
        var scopedReferences = Member("M:Fixture.Scoped.Operations.Run").GetProperty("contextReferenceIds").EnumerateArray().Select(id => id.GetString()).ToArray();
        Assert.Single(rootReferences);
        Assert.Equal(2, scopedReferences.Length);
        Assert.Equal(rootReferences, scopedReferences.Take(1));
        Assert.NotEqual(rootReferences[0], scopedReferences[1]);
        Assert.Equal(rootReferences, Member("M:Fixture.App.A(System.Int32)").GetProperty("contextReferenceIds").EnumerateArray().Select(id => id.GetString()));
        Assert.DoesNotContain("disclose credentials", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        var executable = facts.RootElement.GetProperty("executableTargetIds").EnumerateArray().Select(id => id.GetString()).ToHashSet();
        Assert.Equal(7, executable.Count);
        Assert.DoesNotContain(Member("P:Fixture.Metadata.Value").GetProperty("targetId").GetString(), executable);
        Assert.DoesNotContain(Member("T:Fixture.Metadata").GetProperty("targetId").GetString(), executable);
        Assert.Contains(fixture.Provider.RequestBodies.Skip(1), body => Encoding.UTF8.GetString(body).Contains("disclose credentials", StringComparison.Ordinal));
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(4, checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        AssertObservationPrivacy(fixture, result, checkpoint);
        await fixture.AssertSourceUnchanged();
    }

    private static JsonElement EvidenceUsage(CampaignCliProcessTests.ProcessResult result)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("invocationUsage").Clone();
    }

    private static void AssertObservationPrivacy(Fixture fixture, CampaignCliProcessTests.ProcessResult result,
        CampaignCheckpointArtifact checkpoint)
    {
        var observations = result.Stdout + result.Stderr + Encoding.UTF8.GetString(checkpoint.ExactUtf8Json.AsSpan())
            + fixture.GitHub.CoordinationDocument().ToJsonString();
        foreach (var forbidden in new[] { Fixture.Placeholder, fixture.Repository.Root, fixture.Outside,
            "synthetic-campaign-continuation", "synthetic-shared-repository-read", "reasoning_content" })
            Assert.DoesNotContain(forbidden, observations, StringComparison.Ordinal);
    }
}
