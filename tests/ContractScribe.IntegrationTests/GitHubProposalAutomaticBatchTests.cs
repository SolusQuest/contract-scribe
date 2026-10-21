using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Cli;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("historical")]
    [InlineData("deferred")]
    [InlineData("none")]
    public async Task Automatic_batch_terminal_counts_processed_unsupported_targets_without_counting_future_work(string unsupported)
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: unsupported != "none", newPath: unsupported != "none");
        if (unsupported != "none")
        {
            var relative = unsupported == "historical" ? "App/App.cs" : "App/Other.cs";
            var path = Path.Join(fixture.Repository.Root, relative);
            var source = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, source.Replace("    public static void Run() { }",
                "    public static int Value { get; set; }", StringComparison.Ordinal));
            fixture.GovernedFiles[relative] = (await File.ReadAllBytesAsync(path), fixture.GovernedFiles[relative].Mode);
            fixture.Source = fixture.GovernedFiles["App/App.cs"].Bytes;
            AdvanceTarget(fixture, fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes),
                "Synthetic property and method in canonical file order\n");
        }
        await SetAutomaticQuota(fixture, 1);
        CampaignWorkItemState? historical = null;
        if (unsupported == "historical")
        {
            AssertResult(await fixture.Run(), 5, "host-failure");
            var before = fixture.Checkpoint();
            Assert.Equal(CampaignTerminalReason.Unresolved, before.State.TerminalOutcome!.Reason);
            historical = Assert.Single(before.State.WorkItems, work => work.Status == CampaignWorkStatus.Closed);
            Assert.Equal(0, historical.OuterAttemptCount);
            Assert.Equal(0, historical.CandidateAttemptCount);
            Assert.Single(before.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.UnsupportedCurrentExecutor);
            Assert.Single(before.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred);
            Assert.Equal(0, fixture.Provider.RequestCount);
        }
        AssertResult(await fixture.Run(), 0, "published");
        var after = fixture.Checkpoint();
        Assert.Single(after.State.WorkItems, work => work.Status == CampaignWorkStatus.Accepted);
        Assert.Equal(unsupported == "historical" ? CampaignTerminalReason.Unresolved : CampaignTerminalReason.AllWorkClosed,
            after.State.TerminalOutcome!.Reason);
        if (historical is not null)
        {
            Assert.Equal(historical, after.State.WorkItems.Single(work => work.WorkItemKey == historical.WorkItemKey));
            Assert.Single(after.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.UnsupportedCurrentExecutor);
            Assert.DoesNotContain(after.State.DispatchObservations, row => row.WorkItemKey == historical.WorkItemKey);
        }
        if (unsupported == "deferred")
            Assert.Single(after.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred && !item.Target.Dispatchable);
        var counts = GateCounts(fixture);
        AssertResult(await fixture.Run(), 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(after.Sha256, fixture.Checkpoint().Sha256);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Automatic_patch_rejected_no_result_batch_preserves_attempts_through_next_batch_recovery()
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true, newPath: true);
        var path = Path.Join(fixture.Repository.Root, "App", "App.cs");
        var source = await File.ReadAllTextAsync(path);
        var firstBreak = source.IndexOf('\n');
        Assert.True(firstBreak >= 0);
        await File.WriteAllTextAsync(path, source[..firstBreak] + "\r\n" + source[(firstBreak + 1)..]);
        var bytes = await File.ReadAllBytesAsync(path);
        fixture.Source = bytes;
        fixture.GovernedFiles["App/App.cs"] = (bytes, fixture.GovernedFiles["App/App.cs"].Mode);
        AdvanceTarget(fixture, fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes),
            "Synthetic base with a mixed-line-ending M2 rejection\n");
        await SetAutomaticQuota(fixture, 1);

        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        Assert.Equal(CampaignTerminalKind.Complete, before.State.TerminalOutcome!.Kind);
        Assert.Null(before.State.CandidateObservation);
        Assert.Null(before.State.ActiveReservation);
        var rejected = Assert.Single(before.State.WorkItems, work => work.Status == CampaignWorkStatus.Closed);
        Assert.Equal(CampaignWorkOutcomeCode.PatchRejected, rejected.ClosedOutcome!.Code);
        Assert.Equal(1, rejected.CandidateAttemptCount);
        var deferred = Assert.Single(before.State.TargetProgress, item => item.Kind == CampaignTargetProgressKind.Deferred);
        var requests = fixture.Provider.RequestCount;

        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_PAUSE"] = "after-next-batch";
        environment["CONTRACTSCRIBE_TEST_GITHUB_RELEASE"] = Path.Join(fixture.Outside, "never-release-rejected-next-batch");
        using (var running = CampaignCliProcessTests.Start(fixture.Args(), environment))
        {
            try
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!File.ReadAllLines(fixture.Observations).Contains("after-next-batch", StringComparer.Ordinal))
                {
                    if (running.Process.HasExited)
                    {
                        var result = await running.CompleteAsync();
                        Assert.Fail($"Next batch was not persisted: exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
                    }
                    Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(2));
                    await Task.Delay(20);
                }
                var persisted = fixture.Checkpoint();
                Assert.True(CampaignStateFactory.IsNoResultBatchSuccessor(before, persisted));
                Assert.Equal(rejected, persisted.State.WorkItems.Single(work => work.WorkItemKey == rejected.WorkItemKey));
                Assert.Equal(before.State.LineageCharges, persisted.State.LineageCharges);
                Assert.Equal(requests, fixture.Provider.RequestCount);
                Assert.Equal(new[] { deferred.Target.TargetKey }, persisted.State.Batch.SelectedTargetKeys);
            }
            finally { await CampaignCliProcessTests.StopAsync(running); }
        }
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        AssertResult(await fixture.Run(), 0, "published");
        var after = fixture.Checkpoint();
        Assert.Equal(before.State.Snapshot, after.State.Snapshot);
        Assert.Equal(rejected, after.State.WorkItems.Single(work => work.WorkItemKey == rejected.WorkItemKey));
        Assert.Equal(before.State.DispatchObservations.Where(row => row.WorkItemKey == rejected.WorkItemKey),
            after.State.DispatchObservations.Where(row => row.WorkItemKey == rejected.WorkItemKey));
        Assert.Equal(new[] { deferred.Target.WorkItemKey }, after.State.CandidateObservation!.AcceptedWorkItemKeys);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var counts = GateCounts(fixture);
        AssertResult(await fixture.Run(), 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        Assert.Equal(after.Sha256, fixture.Checkpoint().Sha256);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Automatic_no_result_batch_advances_exact_deferred_members_and_preserves_closed_history()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4, providerScenario: "closed-proposal");
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        Assert.Equal(2, before.State.Batch.SelectedTargetKeys.Length);
        Assert.Equal(2, before.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(2, before.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        var after = fixture.Checkpoint();
        Assert.Equal(before.State.Snapshot, after.State.Snapshot);
        Assert.NotEqual(before.State.Batch.Identity, after.State.Batch.Identity);
        Assert.Empty(before.State.Batch.SelectedTargetKeys.Intersect(after.State.Batch.SelectedTargetKeys));
        Assert.Equal(4, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        foreach (var work in before.State.WorkItems.Where(item => item.OuterAttemptCount > 0))
            Assert.Equal(work, after.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey));
        Assert.True(after.State.LineageCharges.ProviderRequests.TotalCharged > before.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.True(fixture.Provider.RequestCount > requests);
        var finalRequests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        Assert.Equal(finalRequests, fixture.Provider.RequestCount);
        Assert.Equal(after.Sha256, fixture.Checkpoint().Sha256);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Automatic_no_result_closed_prefix_is_retained_when_the_next_batch_publishes()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4);
        await SetAutomaticQuota(fixture, 2);
        fixture.Provider.SkipProposals = true;
        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        fixture.Provider.SkipProposals = false;
        AssertResult(await fixture.Run(), 0, "published");
        var after = fixture.Checkpoint();
        Assert.Equal(before.State.Snapshot, after.State.Snapshot);
        Assert.NotEqual(before.State.Batch.Identity, after.State.Batch.Identity);
        Assert.Equal(2, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(2, after.State.WorkItems.Count(item => item.Status == CampaignWorkStatus.Accepted));
        Assert.Equal(CampaignTerminalReason.Unresolved, after.State.TerminalOutcome!.Reason);
        foreach (var work in before.State.WorkItems.Where(item => item.OuterAttemptCount > 0))
            Assert.Equal(work, after.State.WorkItems.Single(item => item.WorkItemKey == work.WorkItemKey));
        var selected = after.State.Batch.CompleteTargets
            .Where(target => after.State.Batch.SelectedTargetKeys.Contains(target.TargetKey))
            .Select(target => target.WorkItemKey).ToHashSet(StringComparer.Ordinal);
        Assert.True(selected.SetEquals(after.State.CandidateObservation!.AcceptedWorkItemKeys));
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var counts = GateCounts(fixture);
        AssertResult(await fixture.Run(), 0, "awaiting-review");
        Assert.Equal(counts, GateCounts(fixture));
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Automatic_zero_quota_stays_empty_then_positive_quota_admits_initial_members()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 3);
        await SetAutomaticQuota(fixture, 0);
        AssertResult(await fixture.Run(), 0, "no-op");
        var empty = fixture.Checkpoint();
        AssertResult(await fixture.Run(), 0, "no-op");
        Assert.Equal(empty.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(0, fixture.Provider.RequestCount);
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 0, "published");
        var next = fixture.Checkpoint();
        Assert.Equal(empty.State.Snapshot, next.State.Snapshot);
        Assert.Equal(2, next.State.Batch.SelectedTargetKeys.Length);
        Assert.Equal(2, next.State.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
    }

    [Fact]
    public async Task Automatic_operator_merges_reaudit_actual_twelve_to_seven_to_two_to_zero_targets()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 12);
        // Four generations of authenticated reads share one service/database.
        // Retain the ordinary per-scenario guard elsewhere and all request history.
        fixture.GitHub.MaximumRequests = 4 * 20_000;
        await SetAutomaticQuota(fixture, 5);
        foreach (var remaining in new[] { 12, 7, 2 })
        {
            var posts = fixture.GitHub.PrAttempts;
            AssertResult(await fixture.Run(), 0, "published");
            var checkpoint = fixture.Checkpoint();
            Assert.Equal(remaining, checkpoint.State.Batch.CompleteTargets.Length);
            Assert.Equal(Math.Min(5, remaining), checkpoint.State.WorkItems.Count(work => work.Status == CampaignWorkStatus.Accepted));
            Assert.Equal(posts + 1, fixture.GitHub.PrAttempts);
            var requests = fixture.Provider.RequestCount;
            var counts = GateCounts(fixture);
            AssertResult(await fixture.Run(), 0, "awaiting-review");
            Assert.Equal(counts, GateCounts(fixture));
            Assert.Equal(requests, fixture.Provider.RequestCount);
            await OperatorMergeLatest(fixture);
        }
        AssertResult(await fixture.Run(), 0, "no-op");
        Assert.Empty(fixture.Checkpoint().State.Batch.CompleteTargets);
        var finalCounts = GateCounts(fixture);
        var finalRequests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 0, "no-op");
        Assert.Equal(finalRequests, fixture.Provider.RequestCount);
        Assert.Equal(3, fixture.GitHub.PrAttempts);
        Assert.InRange(fixture.GitHub.Requests.Count, 1, fixture.GitHub.MaximumRequests);
        Assert.Equal(finalCounts.Created, GateCounts(fixture).Created);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Historical_terminal_publication_allows_later_no_result_batch_without_mutating_the_original_PR()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4);
        await SetAutomaticQuota(fixture, 1);
        AssertResult(await fixture.Run(), 0, "published");
        var original = fixture.GitHub.PullRequests[0].DeepClone();
        await OperatorMergeLatest(fixture);
        fixture.Provider.SkipProposals = true;
        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        Assert.Equal(3, before.State.Batch.CompleteTargets.Length);
        var publication = fixture.GitHub.CoordinationDocument()["publication"]!.DeepClone();
        var head = fixture.GitHub.PullRequests[0]["head"]!.DeepClone();
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        var after = fixture.Checkpoint();
        Assert.NotEqual(before.State.Batch.Identity, after.State.Batch.Identity);
        Assert.Equal(before.State.Snapshot, after.State.Snapshot);
        Assert.Equal(2, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(1, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred));
        Assert.Equal(publication.ToJsonString(), fixture.GitHub.CoordinationDocument()["publication"]!.ToJsonString());
        Assert.Equal(head.ToJsonString(), fixture.GitHub.PullRequests[0]["head"]!.ToJsonString());
        Assert.Equal(original["number"]!.GetValue<int>(), fixture.GitHub.PullRequests[0]["number"]!.GetValue<int>());
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.True(fixture.Provider.RequestCount > requests);
        Assert.True(after.State.LineageCharges.ProviderRequests.TotalCharged > before.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.State.Predecessor),
            System.Text.Json.JsonSerializer.Serialize(after.State.Predecessor));
        AssertResult(await fixture.Run(), 5, "host-failure");
        var end = fixture.Checkpoint();
        Assert.Equal(3, end.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        var finalRequests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        Assert.Equal(finalRequests, fixture.Provider.RequestCount);
        Assert.Equal(end.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
    }

    [Theory]
    [InlineData("before-next-batch")]
    [InlineData("after-next-batch")]
    public async Task Automatic_no_result_batch_recovers_after_real_process_kill_at_the_accepted_boundary(string boundary)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4, providerScenario: "closed-proposal");
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        var requests = fixture.Provider.RequestCount;
        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_PAUSE"] = boundary;
        environment["CONTRACTSCRIBE_TEST_GITHUB_RELEASE"] = Path.Join(fixture.Outside, "never-release-next-batch");
        using var running = CampaignCliProcessTests.Start(fixture.Args(), environment);
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(fixture.Observations) || !File.ReadAllLines(fixture.Observations).Contains(boundary, StringComparer.Ordinal))
            {
                Assert.False(running.Process.HasExited);
                Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(2));
                await Task.Delay(20);
            }
            var interrupted = fixture.Checkpoint();
            Assert.Equal(boundary == "before-next-batch", interrupted.State.Batch.Identity == before.State.Batch.Identity);
            Assert.Equal(requests, fixture.Provider.RequestCount);
            Assert.Equal(before.State.LineageCharges, interrupted.State.LineageCharges);
        }
        finally { await CampaignCliProcessTests.StopAsync(running); }
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        AssertResult(await fixture.Run(), 5, "host-failure");
        Assert.Equal(4, fixture.Checkpoint().State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        var finalRequests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 5, "host-failure");
        Assert.Equal(finalRequests, fixture.Provider.RequestCount);
    }

    [Fact]
    public async Task Materialized_instruction_drift_selects_fresh_correctness_without_reusing_closed_results()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync(targetCount: 4, providerScenario: "closed-proposal");
        await SetAutomaticQuota(fixture, 2);
        AssertResult(await fixture.Run(), 5, "host-failure");
        var before = fixture.Checkpoint();
        var initialBinding = await MaterializedBinding(fixture);
        var target = fixture.GitHub.Refs["refs/heads/main"];
        var path = Path.Join(fixture.Repository.Root, "AGENTS.md");
        await File.WriteAllTextAsync(path, "Use concise documentation for these synthetic methods.\n");
        Assert.NotEqual(initialBinding, await MaterializedBinding(fixture));
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run(), 4, "stale");
        Assert.Equal(before.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(requests, fixture.Provider.RequestCount);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        files["AGENTS.md"] = await File.ReadAllBytesAsync(path);
        fixture.GovernedFiles["AGENTS.md"] = (files["AGENTS.md"], File.GetUnixFileMode(path));
        AdvanceTarget(fixture, files, "Operator changes the actual applicable instruction\n");
        AssertResult(await fixture.Run(), 5, "host-failure");
        var after = fixture.Checkpoint();
        Assert.NotEqual(target, fixture.GitHub.Refs["refs/heads/main"]);
        Assert.NotEqual(before.State.Snapshot.OpaqueSnapshotBinding, after.State.Snapshot.OpaqueSnapshotBinding);
        Assert.NotEqual(before.State.Snapshot.ExecutionCommitmentSha256, after.State.Snapshot.ExecutionCommitmentSha256);
        Assert.Equal(before.Sha256, after.State.Predecessor!.FinalCheckpointSha256);
        Assert.Equal(2, after.State.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Skipped));
        Assert.True(after.State.LineageCharges.ProviderRequests.TotalCharged > before.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
    }

    private static async Task<string> MaterializedBinding(Fixture fixture)
    {
        var arguments = GitHubProposalCommandParser.Parse(fixture.Args().AsSpan(1)).Arguments!;
        var campaign = GitHubCampaignConfigurationReader.Read(arguments.CampaignFile, fixture.Repository.Root);
        var preflight = CampaignPreflight.Run(arguments.Campaign(campaign), fixture.Repository.Root,
            CliBuildIdentity.Current, remoteCheckpoint: true) with
        { SnapshotBinding = "snapshot.synthetic.materialized" };
        string? binding = null;
        var host = new ProductionRepositorySessionHost(new HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
        await host.RunAsync(new(fixture.Repository.Root, preflight.InputPath, preflight.PolicyBytes,
            PublicationTarget: null, PublishResult: false), new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
        {
            var configuration = preflight.Configuration.Document;
            binding = CampaignCommandRunner.CreatePlanningInput(preflight, configuration,
                configuration.CreateExecutionPolicy(), bundle, token).Snapshot.OpaqueSnapshotBinding;
            return Task.CompletedTask;
        }), CancellationToken.None);
        return Assert.IsType<string>(binding);
    }

    private static async Task OperatorMergeLatest(Fixture fixture)
    {
        var index = fixture.GitHub.PullRequests.Count - 1;
        var head = fixture.GitHub.PullRequests[index]["head"]!["sha"]!.GetValue<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(string tree, string prefix)
        {
            foreach (var entry in fixture.GitHub.Trees[tree])
            {
                if (entry.Mode == "040000") Walk(entry.Oid, prefix + entry.Path + "/");
                else files.Add(prefix + entry.Path, fixture.GitHub.Blobs[entry.Oid]);
            }
        }
        Walk(fixture.GitHub.Commits[head]["tree"]!["sha"]!.GetValue<string>(), "");
        foreach (var pair in files)
        {
            var path = Path.Join(fixture.Repository.Root, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, pair.Value);
            var mode = fixture.GovernedFiles.TryGetValue(pair.Key, out var prior) ? prior.Mode : null;
            fixture.GovernedFiles[pair.Key] = (pair.Value, mode);
        }
        fixture.Source = files["App/App.cs"];
        fixture.GitHub.Lifecycle("merged", index);
        AdvanceTarget(fixture, files, "Operator merges exact accepted proposal bytes\n");
    }

    private static async Task SetAutomaticQuota(Fixture fixture, int maximumTargets)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        config["budgets"]!["invocation"] = new JsonObject { ["maximumTargets"] = maximumTargets, ["maximumProviderRequests"] = 64 };
        await File.WriteAllTextAsync(fixture.Configuration!, config.ToJsonString());
    }
}
