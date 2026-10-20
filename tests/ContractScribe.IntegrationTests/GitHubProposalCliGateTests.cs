using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task G3_open_PR_lifecycle_precedes_discovery_and_provider_even_with_changed_configuration(bool ready, bool changedHead)
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        var initial = await fixture.Run("start");
        Assert.True(initial.ExitCode == 0, initial.Stdout + initial.Stderr + await File.ReadAllTextAsync(fixture.Observations));
        AssertResult(initial, 0, "published");
        var checkpoint = fixture.Checkpoint();
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.True(Observed(fixture, "provider-create") > 0);
        if (ready) fixture.GitHub.Lifecycle("ready");
        if (changedHead)
        {
            var head = AdvanceReadme(fixture);
            // GitHub's live base.sha can advance while immutable operation base stays unchanged.
            fixture.GitHub.PullRequests[0]["base"]!["sha"] = head;
        }
        await ConfigureAppend(fixture);
        fixture.Snapshot = "changed-caller-snapshot";
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["provider"]!["model"] = "changed-model";
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 200;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var before = GateCounts(fixture);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), changedHead ? 4 : 0, changedHead ? "stale" : "awaiting-review");
        Assert.Equal(before, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        Assert.Equal(checkpoint.Sha256, fixture.Checkpoint().Sha256);
        Assert.Single(fixture.GitHub.PullRequests);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task G3_checkout_binding_rejects_local_only_and_remote_only_compile_glob_members(bool remoteOnly, bool outsideProject)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = Encoding.UTF8.GetBytes("namespace Fixture; public class Added { public void Missing() { } }\n");
        var sourcePath = outsideProject ? "Shared/Added.cs" : "App/Added.cs";
        if (outsideProject)
        {
            var projectPath = Path.Join(fixture.Repository.Root, "App", "App.csproj");
            var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
                "<ItemGroup><Compile Include=\"../Shared/**/*.cs\" /></ItemGroup></Project>", StringComparison.Ordinal);
            await File.WriteAllTextAsync(projectPath, project);
            fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), fixture.GovernedFiles["App/App.csproj"].Mode);
        }
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        if (remoteOnly)
        {
            files.Add(sourcePath, source);
            AdvanceTarget(fixture, files, "Add remote-only compile glob member\n");
        }
        else
        {
            if (outsideProject) AdvanceTarget(fixture, files, "Bind external compile glob\n");
            var path = Path.Join(fixture.Repository.Root, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, source);
        }
        var result = await fixture.Run("start");
        AssertResult(result, 4, "stale");
        Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.Equal(0, Observed(fixture, "provider-create"));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G3_terminal_deleted_branch_allows_fresh_Audit_and_retained_closed_base_blocks_a_return(bool merged)
    {
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var prior = fixture.Checkpoint();
        fixture.GitHub.Lifecycle(merged ? "merged" : "closed");
        var proposal = fixture.GitHub.Refs.Keys.Single(key => key.Contains("/proposals/", StringComparison.Ordinal));
        fixture.GitHub.Refs.Remove(proposal);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        if (!merged)
        {
            var before = GateCounts(fixture);
            AssertResult(await fixture.Run("resume"), 4, "closed-unmerged");
            Assert.Equal(before, GateCounts(fixture));
        }
        var current = AdvanceReadme(fixture);
        fixture.GitHub.PullRequests[0]["base"]!["sha"] = current;
        // Keep the old caller expectedBase and operation: neither is a generic successor approval.
        var discovery = Observed(fixture, "sdk-discovery");
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run("resume"), 0, "no-op");
        var fresh = fixture.Checkpoint();
        Assert.Equal(discovery + 1, Observed(fixture, "sdk-discovery"));
        Assert.Equal(requests, fixture.Provider.RequestCount);
        Assert.NotEqual(prior.State.Snapshot, fresh.State.Snapshot);
        Assert.Null(fresh.State.CandidateObservation);
        Assert.All(fresh.State.WorkItems, work => Assert.Null(work.TrustedProposal));
        Assert.Contains(fresh.State.Batch.CompleteTargets, target => target.BatchEligible);
        Assert.Equal(prior.State.LineageCharges, fresh.State.LineageCharges);
        Assert.Equal(prior.Sha256, fresh.State.Predecessor!.FinalCheckpointSha256);
        if (!merged)
        {
            fixture.GitHub.Refs["refs/heads/main"] = fixture.GitHub.BaseOid;
            var before = GateCounts(fixture);
            AssertResult(await fixture.Run("resume"), 4, "closed-unmerged");
            Assert.Equal(before, GateCounts(fixture));
            Assert.Equal(fresh.Sha256, fixture.Checkpoint().Sha256);
        }
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task G3_unpublished_quota_changes_restore_original_manifest_and_accepted_progress(int quota)
    {
        await using var fixture = await Fixture.CreateAsync(twoWorks: true);
        var prior = await InterruptAcceptedUnpublished(fixture);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = quota;
        configuration["budgets"]!["invocation"]!["maximumProviderRequests"] = 64;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run("resume"), 0, "published");
        var restored = fixture.Checkpoint();
        Assert.Equal(prior.State.Batch.Identity, restored.State.Batch.Identity);
        Assert.Equal(prior.State.Batch.SelectedTargetKeys.ToArray(), restored.State.Batch.SelectedTargetKeys.ToArray());
        Assert.Equal(prior.State.Batch.CreationQuota, restored.State.Batch.CreationQuota);
        Assert.Equal(requests, fixture.Provider.RequestCount);
        Assert.True(restored.State.LineageCharges.ProviderRequests.TotalCharged >= prior.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.Single(restored.State.WorkItems, work => work.Status == CampaignWorkStatus.Accepted);
    }

    private static int Observed(Fixture fixture, string name) => File.Exists(fixture.Observations)
        ? File.ReadAllLines(fixture.Observations).Count(line => line == name) : 0;

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task G3_actual_Audit_base_does_not_rewrite_the_callers_publication_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var actual = AdvanceReadme(fixture);
        AssertResult(await fixture.Run("start"), 4, "local-invalid");
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.NotNull(fixture.Checkpoint().State.CandidateObservation);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
        var requests = fixture.Provider.RequestCount;
        fixture.Publication["expectedBaseCommitOid"] = actual;
        AssertResult(await fixture.Run("resume"), 0, "published");
        Assert.Equal(requests, fixture.Provider.RequestCount);
        Assert.Single(fixture.GitHub.PullRequests);
        var before = GateCounts(fixture);
        AssertResult(await fixture.Run("resume"), 0, "awaiting-review");
        Assert.Equal(before, GateCounts(fixture));
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("policy")]
    [InlineData("profile")]
    [InlineData("style")]
    [InlineData("attempts")]
    [InlineData("candidates")]
    public async Task G3_unpublished_correctness_change_creates_fresh_work_and_retains_archived_usage(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await InterruptAcceptedUnpublished(fixture);
        await ArchiveRemoteCheckpoint(fixture);
        var prior = fixture.Checkpoint();
        var archive = fixture.GitHub.CoordinationDocument()["archive"]!.ToJsonString();
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        if (change == "style") configuration["scribeRequest"] = new JsonObject
        {
            ["styleProfileTemplate"] = new JsonObject { ["styleProfileId"] = "style.changed" },
        };
        if (change == "attempts") configuration["budgets"]!["campaign"]!["maximumAttemptsPerTarget"] = 3;
        if (change == "candidates") configuration["budgets"]!["campaign"]!["maximumCandidatesPerBlock"] = 31;
        if (change == "profile")
        {
            configuration["planning"] = new JsonObject { ["targetProfile"] = "profile.assembly-visible" };
            var policy = await File.ReadAllTextAsync(Path.Join(fixture.Repository.Root, "policy.json"));
            await File.WriteAllTextAsync(Path.Join(fixture.Repository.Root, "policy.json"),
                policy.Replace("profile.external-api", "profile.assembly-visible", StringComparison.Ordinal));
        }
        if (change == "policy") await File.WriteAllTextAsync(Path.Join(fixture.Repository.Root, "policy.json"),
            CampaignCliProcessTests.OptionalPolicy);
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var requests = fixture.Provider.RequestCount;
        AssertResult(await fixture.Run("resume"), 0, "no-op");
        var fresh = fixture.Checkpoint();
        Assert.NotEqual(prior.State.Snapshot, fresh.State.Snapshot);
        Assert.Null(fresh.State.CandidateObservation);
        Assert.All(fresh.State.WorkItems, work => Assert.Null(work.TrustedProposal));
        Assert.Equal(prior.State.LineageCharges, fresh.State.LineageCharges);
        Assert.Equal(prior.State.ArchivedDispatchPrefix, fresh.State.ArchivedDispatchPrefix);
        Assert.Equal(archive, fixture.GitHub.CoordinationDocument()["archive"]!.ToJsonString());
        Assert.Equal(requests, fixture.Provider.RequestCount);
        Assert.Empty(fixture.GitHub.PullRequests);
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task G3_product_owned_retry_override_cannot_replace_unpublished_recovery_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prior = await InterruptAcceptedUnpublished(fixture);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["retry"] = new JsonObject { ["retryPolicyId"] = "retry.changed", ["retryPolicyVersion"] = 1 };
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var before = GateCounts(fixture);
        AssertResult(await fixture.Run("resume"), 4, "local-invalid");
        Assert.Equal(before, GateCounts(fixture));
        Assert.Equal(prior.Sha256, fixture.Checkpoint().Sha256);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("local-only")]
    [InlineData("remote-only")]
    [InlineData("matched")]
    public async Task G3_external_AdditionalFiles_membership_is_bound_even_when_the_local_glob_is_empty(string membership)
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectPath = Path.Join(fixture.Repository.Root, "App", "App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            "<ItemGroup><AdditionalFiles Include=\"../Shared/**/*.json\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), fixture.GovernedFiles["App/App.csproj"].Mode);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        var rules = "{\"rule\":\"synthetic\"}\n"u8.ToArray();
        if (membership != "local-only") files.Add("Shared/rules.json", rules);
        if (membership == "matched") files.Add("Shared/README.md", "Unrelated documentation.\n"u8.ToArray());
        AdvanceTarget(fixture, files, "Bind external semantic input\n");
        if (membership != "remote-only")
        {
            Directory.CreateDirectory(Path.Join(fixture.Repository.Root, "Shared"));
            await File.WriteAllBytesAsync(Path.Join(fixture.Repository.Root, "Shared", "rules.json"), rules);
        }
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var result = await fixture.Run("start");
        AssertResult(result, membership == "matched" ? 0 : 4, membership == "matched" ? "no-op" : "stale");
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.Equal(0, Observed(fixture, "provider-create"));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task G3_documentation_only_change_inside_a_project_directory_allows_fresh_Audit()
    {
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var prior = fixture.Checkpoint();
        fixture.GitHub.Lifecycle("merged");
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        files.Add("App/README.md", "Unrelated project documentation.\n"u8.ToArray());
        AdvanceTarget(fixture, files, "Update project documentation only\n");
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var before = GateCounts(fixture);
        AssertResult(await fixture.Run("resume"), 0, "no-op");
        Assert.Equal(before.Discovery + 1, Observed(fixture, "sdk-discovery"));
        Assert.Equal(before.Physical, fixture.Provider.RequestCount);
        Assert.NotEqual(prior.State.Snapshot, fixture.Checkpoint().State.Snapshot);
        Assert.Null(fixture.Checkpoint().State.CandidateObservation);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("integrity", "commits", 429, 3, "rate-limit")]
    [InlineData("integrity", "trees", 429, 3, "rate-limit")]
    [InlineData("integrity", "blobs", 429, 3, "rate-limit")]
    [InlineData("checkout", "commits", 429, 3, "rate-limit")]
    [InlineData("checkout", "trees", 429, 3, "rate-limit")]
    [InlineData("integrity", "blobs", 403, 4, "permission")]
    [InlineData("checkout", "commits", 503, 5, "host-failure")]
    public async Task G3_authenticated_object_reads_preserve_service_failures_before_provider(
        string phase, string kind, int status, int exit, string outcome)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (phase == "integrity") AssertResult(await fixture.Run("start"), 0, "published");
        var before = GateCounts(fixture);
        var detailSeen = false;
        var rejected = false;
        fixture.GitHub.Before = request =>
        {
            if (request.Path == "/repos/Owner/repo/pulls/1") detailSeen = true;
            if (request.Method == "GET" && request.Path.Contains("/git/" + kind + "/", StringComparison.Ordinal)
                && (phase == "integrity" ? detailSeen : Observed(fixture, "sdk-discovery") > before.Discovery))
            {
                fixture.GitHub.RejectionStatus = status;
                fixture.GitHub.RejectPath = request.Path;
                rejected = true;
            }
        };
        AssertResult(await fixture.Run(phase == "integrity" ? "resume" : "start"), exit, outcome);
        Assert.True(rejected);
        var after = GateCounts(fixture);
        Assert.Equal(before.Created, after.Created);
        Assert.Equal(before.Sent, after.Sent);
        Assert.Equal(before.Physical, after.Physical);
        if (phase == "integrity") Assert.Equal(before.Discovery, after.Discovery);
        else Assert.Equal(before.Discovery + 1, after.Discovery);
    }
    private static (int Discovery, int Created, int Sent, int Physical) GateCounts(Fixture fixture) =>
        (Observed(fixture, "sdk-discovery"), Observed(fixture, "provider-create"),
            Observed(fixture, "provider-send"), fixture.Provider.RequestCount);

    private static string AdvanceReadme(Fixture fixture)
    {
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        files["README.md"] = "A new documentation-only base.\n"u8.ToArray();
        return AdvanceTarget(fixture, files, "README-only target advancement\n");
    }

    private static string AdvanceTarget(Fixture fixture, IReadOnlyDictionary<string, byte[]> files, string message)
    {
        lock (fixture.GitHub.Gate)
        {
            var before = fixture.GitHub.Refs["refs/heads/main"];
            var basis = fixture.GitHub.Commits[before];
            var next = fixture.GitHub.AddCommit(new JsonObject
            {
                ["tree"] = fixture.GitHub.BuildTree(files),
                ["parents"] = new JsonArray(before),
                ["message"] = message,
                ["author"] = basis["author"]!.DeepClone(),
                ["committer"] = basis["committer"]!.DeepClone(),
            });
            fixture.GitHub.Refs["refs/heads/main"] = next;
            return next;
        }
    }

    private static async Task<CampaignCheckpointArtifact> InterruptAcceptedUnpublished(Fixture fixture)
    {
        var acknowledgement = Path.Join(fixture.Outside, "unpublished-accepted.ack");
        var environment = fixture.Environment();
        foreach (var pair in CampaignCliProcessTests.HookEnvironment(CampaignProcessBoundaryHooks.PatchAfterAcceptedReadback,
            acknowledgement, Path.Join(fixture.Outside, "unpublished-never-release"))) environment[pair.Key] = pair.Value;
        using var interrupted = CampaignCliProcessTests.Start(fixture.Args("start"), environment);
        CampaignCheckpointArtifact accepted;
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(acknowledgement, interrupted,
                CampaignProcessBoundaryHooks.PatchAfterAcceptedReadback, TimeSpan.FromMinutes(2));
            accepted = fixture.Checkpoint();
            Assert.NotNull(accepted.State.CandidateObservation);
            Assert.Empty(fixture.GitHub.PullRequests);
        }
        finally { await CampaignCliProcessTests.StopAsync(interrupted); }
        fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
        return accepted;
    }
}
