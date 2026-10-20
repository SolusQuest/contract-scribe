using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("readback")]
    public async Task G3_caller_cancellation_during_initial_checkpoint_IO_preserves_cancelled(string phase)
    {
        await using var fixture = await Fixture.CreateAsync();
        var acknowledgement = Path.Join(fixture.Outside, "initial-checkpoint.ack");
        var release = Path.Join(fixture.Outside, "initial-checkpoint.release");
        var environment = fixture.Environment();
        foreach (var pair in CampaignCliProcessTests.HookEnvironment(CampaignProcessBoundaryHooks.InitialBeforeCreate,
            acknowledgement, release)) environment[pair.Key] = pair.Value;
        environment["CONTRACTSCRIBE_TEST_GITHUB_TIMEOUT"] = "30000";
        var responseRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var running = CampaignCliProcessTests.Start(fixture.Args("start"), environment);
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(acknowledgement, running,
                CampaignProcessBoundaryHooks.InitialBeforeCreate, TimeSpan.FromMinutes(2));
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkpointWritten = false;
            fixture.GitHub.After = request =>
            {
                if (request.Method == "POST" && request.Path == "/graphql") checkpointWritten = true;
            };
            fixture.GitHub.BeforeResponse = request =>
            {
                if (held.Task.IsCompleted) return null;
                var selected = phase == "write"
                    ? request.Method == "POST" && request.Path.EndsWith("/git/blobs", StringComparison.Ordinal)
                    : request.Method == "GET" && request.Path == "/repos/Owner/repo"
                        && (phase == "read" || checkpointWritten);
                if (!selected) return null;
                held.TrySetResult();
                return responseRelease.Task;
            };
            await File.WriteAllTextAsync(release, "continue\n");
            await held.Task.WaitAsync(TimeSpan.FromMinutes(2));
            using var signal = Process.Start(new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-TERM", running.Process.Id.ToString(CultureInfo.InvariantCulture) },
            })!;
            await signal.WaitForExitAsync();
            Assert.Equal(0, signal.ExitCode);
            var elapsed = Stopwatch.StartNew();
            while (Observed(fixture, "signal-observed") == 0)
            {
                Assert.False(running.Process.HasExited);
                Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(2));
                await Task.Delay(10);
            }
            // Only the original response is held. Independent bounded readback
            // must remain available to reconcile an already dispatched write.
            responseRelease.TrySetResult();
            await running.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            AssertResult(await running.CompleteAsync(), 6, "cancelled");
            Assert.Equal(0, Observed(fixture, "provider-create"));
            Assert.Equal(0, fixture.Provider.RequestCount);
            Assert.Empty(fixture.GitHub.PullRequests);
        }
        finally
        {
            responseRelease.TrySetResult();
            await CampaignCliProcessTests.StopAsync(running);
        }
    }

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

    private static void WriteBindingGenerator(string path)
    {
        var source = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            using Microsoft.CodeAnalysis;
            using System.Linq;
            [Generator]
            public sealed class BindingGenerator : ISourceGenerator
            {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context)
                {
                    var input = string.Join(",", context.AdditionalFiles.Select(file => file.GetText()?.ToString()));
                    context.AddSource("Bound.g.cs", "// Actual bound generator output\n// " + input.Replace("\n", "\n// "));
                }
            }
            """);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Microsoft.CodeAnalysis.ISourceGenerator).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("BindingGenerator", [source], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var stream = File.Create(path);
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
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
    [InlineData("import", "changed")]
    [InlineData("import", "local-only")]
    [InlineData("import", "remote-only")]
    [InlineData("import", "matched")]
    [InlineData("analyzer", "changed")]
    [InlineData("analyzer", "local-only")]
    [InlineData("analyzer", "remote-only")]
    [InlineData("analyzer", "matched")]
    [InlineData("reference", "changed")]
    [InlineData("reference", "local-only")]
    [InlineData("reference", "remote-only")]
    [InlineData("reference", "matched")]
    public async Task G3_checkout_binding_rejects_changed_evaluated_build_inputs(string kind, string membership)
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectPath = Path.Join(fixture.Repository.Root, "App", "App.csproj");
        var path = kind switch
        {
            "import" => "Build/custom.buildinput",
            "analyzer" => "Analyzers/BindingGenerator.dll",
            _ => "References/Dependency.dll",
        };
        var project = await File.ReadAllTextAsync(projectPath);
        {
            project = project.Replace("</Project>", kind == "import"
                ? "<Import Project=\"../Build/custom.buildinput\" Condition=\"Exists('../Build/custom.buildinput')\" /></Project>"
                : kind == "analyzer"
                ? "<ItemGroup><Analyzer Include=\"../Analyzers/BindingGenerator.dll\" /></ItemGroup></Project>"
                : "<ItemGroup><Reference Include=\"Dependency\"><HintPath>../References/Dependency.dll</HintPath></Reference></ItemGroup></Project>", StringComparison.Ordinal);
            await File.WriteAllTextAsync(projectPath, project);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(fixture.Repository.Root, path))!);
            if (kind == "import")
                await File.WriteAllTextAsync(Path.Join(fixture.Repository.Root, path),
                    "<Project><PropertyGroup><DefineConstants>$(DefineConstants);LOCAL_BUILD_INPUT</DefineConstants></PropertyGroup></Project>");
            else if (kind == "analyzer") WriteBindingGenerator(Path.Join(fixture.Repository.Root, path));
            else File.Copy(typeof(RepositoryLoader).Assembly.Location, Path.Join(fixture.Repository.Root, path));
        }
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), fixture.GovernedFiles["App/App.csproj"].Mode);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        var local = await File.ReadAllBytesAsync(Path.Join(fixture.Repository.Root, path));
        // Valid local assembly/import is consumed by the real loader; the target
        // commits different bytes, without relying on an unloadable local input.
        files.Remove(path);
        if (membership != "local-only")
            files[path] = membership != "changed" ? local : kind == "import"
                ? Encoding.UTF8.GetBytes("<Project><PropertyGroup><DefineConstants>$(DefineConstants);REMOTE_BUILD_INPUT</DefineConstants></PropertyGroup></Project>")
                : local.Concat(new byte[] { 0 }).ToArray();
        if (membership == "remote-only") File.Delete(Path.Join(fixture.Repository.Root, path));
        if (kind == "analyzer" && membership == "matched")
        {
            var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(fixture.Repository.Root, "Fixture.slnx"));
            Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
            await using var session = loaded.Session!;
            Assert.Contains(session.GeneratedSources, fact => fact.SourceText.Contains("Actual bound generator output", StringComparison.Ordinal));
        }
        AdvanceTarget(fixture, files, "Change evaluated build input only\n");
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        var result = await fixture.Run("start");
        AssertResult(result, membership == "matched" ? 0 : 4, membership == "matched" ? "no-op" : "stale");
        if (membership != "matched") Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.Equal(0, Observed(fixture, "provider-create"));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("remote-only")]
    [InlineData("local-only")]
    [InlineData("matched")]
    [InlineData("dynamic")]
    public async Task G3_nested_custom_import_binds_generated_source_inputs_and_empty_globs(string membership)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            "<Import Project=\"../Build/outer.buildinput\" /><ItemGroup><Analyzer Include=\"../Analyzers/BindingGenerator.dll\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        Directory.CreateDirectory(Path.Join(root, "Build/nested"));
        var declarations = new Dictionary<string, string>
        {
            ["App/App.csproj"] = project,
            ["Build/outer.buildinput"] = "<Project><Import Project=\"$(MSBuildThisFileDirectory)nested/inner.buildinput\" /></Project>",
            ["Build/nested/inner.buildinput"] = membership == "dynamic"
                ? "<Project><ItemGroup><AdditionalFiles Include=\"../Shared/$(UnboundInput)/**/*.rules\" /></ItemGroup></Project>"
                : "<Project><ItemGroup><AdditionalFiles Include=\"../Shared/**/*.rules\" /></ItemGroup></Project>",
        };
        foreach (var pair in declarations)
        {
            await File.WriteAllTextAsync(Path.Join(root, pair.Key), pair.Value);
            fixture.GovernedFiles[pair.Key] = (Encoding.UTF8.GetBytes(pair.Value), null);
        }
        Directory.CreateDirectory(Path.Join(root, "Analyzers"));
        WriteBindingGenerator(Path.Join(root, "Analyzers/BindingGenerator.dll"));
        fixture.GovernedFiles["Analyzers/BindingGenerator.dll"] = (await File.ReadAllBytesAsync(Path.Join(root, "Analyzers/BindingGenerator.dll")), null);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        var rules = "Actual generator additional input\n"u8.ToArray();
        if (membership != "local-only" && membership != "dynamic") files["Shared/input.rules"] = rules;
        if (membership is "local-only" or "matched")
        {
            Directory.CreateDirectory(Path.Join(root, "Shared"));
            await File.WriteAllBytesAsync(Path.Join(root, "Shared/input.rules"), rules);
        }
        if (membership == "matched")
        {
            var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(root, "Fixture.slnx"));
            Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
            await using var session = loaded.Session!;
            Assert.Contains(session.GeneratedSources, fact => fact.SourceText.Contains("Actual generator additional input", StringComparison.Ordinal));
        }
        AdvanceTarget(fixture, files, "Bind nested custom imports and semantic input membership\n");
        await LimitBindingInvocation(fixture);
        var result = await fixture.Run("start");
        AssertResult(result, membership == "matched" ? 0 : 4, membership == "matched" ? "no-op" : "stale");
        if (membership != "matched") Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("inactive-analyzer")]
    [InlineData("inactive-compile")]
    [InlineData("inactive-import")]
    [InlineData("none")]
    [InlineData("content")]
    public async Task G3_unconsumed_and_inactive_declared_files_do_not_become_a_dirty_checkout_gate(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var addition = kind switch
        {
            "inactive-compile" => "<ItemGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Compile Include=\"../Unused/**/*.cs\" /></ItemGroup>",
            "inactive-analyzer" => "<ItemGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Analyzer Include=\"../Unused/**/*.dll\" /></ItemGroup>",
            "inactive-import" => "<Import Project=\"../Unused/custom.buildinput\" Condition=\"'$(TargetFramework)' == 'net9.0'\" />",
            "none" => "<ItemGroup><None Include=\"../Unused/**/*.md\" /></ItemGroup>",
            _ => "<ItemGroup><Content Include=\"../Unused/**/*.md\" /></ItemGroup>",
        };
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>", addition + "</Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        var path = kind switch { "inactive-compile" => "Unused/Added.cs", "inactive-analyzer" => "Unused/Generator.dll", "inactive-import" => "Unused/custom.buildinput", _ => "Unused/README.md" };
        files[path] = "Unrelated remote-only input.\n"u8.ToArray();
        AdvanceTarget(fixture, files, "Add inactive or noncompiler files\n");
        await LimitBindingInvocation(fixture);
        AssertResult(await fixture.Run("start"), 0, "no-op");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("obj")]
    [InlineData("bin")]
    public async Task G3_local_only_declared_output_input_cannot_hide_behind_SDK_output_exclusion(string output)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            $"<ItemGroup><AdditionalFiles Include=\"{output}/semantic-inputs/**/*.json\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var input = Path.Join(root, "App", output, "semantic-inputs/input.json");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        await File.WriteAllTextAsync(input, "{\"rule\":\"unbound-local-input\"}");
        AdvanceTarget(fixture, fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes), "Declare semantic output input without committing its bytes\n");
        await LimitBindingInvocation(fixture);
        var result = await fixture.Run("start");
        AssertResult(result, 4, "stale");
        Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G3_reference_file_paths_bind_repository_inputs_without_rebinding_external_assemblies(bool external)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var reference = external ? typeof(RepositoryLoader).Assembly.Location : "../References/Dependency.dll";
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            $"<ItemGroup><Reference Include=\"{System.Security.SecurityElement.Escape(reference)}\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        // Missing local direct-reference path must remain visible on the target.
        if (!external) files["References/Dependency.dll"] = await File.ReadAllBytesAsync(typeof(RepositoryLoader).Assembly.Location);
        AdvanceTarget(fixture, files, "Bind direct reference paths\n");
        await LimitBindingInvocation(fixture);
        var result = await fixture.Run("start");
        AssertResult(result, external ? 0 : 4, external ? "no-op" : "stale");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("local-only")]
    [InlineData("remote-only")]
    [InlineData("changed")]
    [InlineData("matched")]
    public async Task G3_compiler_metadata_documentation_sidecars_are_bound_with_the_reference(string membership)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        Directory.CreateDirectory(Path.Join(root, "References"));
        var assembly = Path.Join(root, "References/Dependency.dll");
        File.Copy(typeof(RepositoryLoader).Assembly.Location, assembly);
        var projectPath = Path.Join(root, "App/App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            "<ItemGroup><Reference Include=\"Dependency\"><HintPath>../References/Dependency.dll</HintPath></Reference></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        fixture.GovernedFiles["References/Dependency.dll"] = (await File.ReadAllBytesAsync(assembly), null);
        const string local = "<doc><members><member name=\"T:ContractScribe.Roslyn.RepositoryLoader\"><summary>Local metadata documentation.</summary></member></members></doc>";
        if (membership != "remote-only") await File.WriteAllTextAsync(Path.ChangeExtension(assembly, ".xml"), local);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        if (membership != "local-only") files["References/Dependency.xml"] = Encoding.UTF8.GetBytes(membership == "changed"
            ? local.Replace("Local metadata", "Remote metadata", StringComparison.Ordinal) : local);
        if (membership == "matched")
        {
            var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(root, "Fixture.slnx"));
            Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
            await using var session = loaded.Session!;
            var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
            var symbol = app.Compilation.GetTypeByMetadataName("ContractScribe.Roslyn.RepositoryLoader");
            Assert.NotNull(symbol);
            Assert.Contains("Local metadata documentation", symbol.GetDocumentationCommentXml(), StringComparison.Ordinal);
        }
        AdvanceTarget(fixture, files, "Bind compiler metadata documentation input\n");
        await LimitBindingInvocation(fixture);
        AssertResult(await fixture.Run("start"), membership == "matched" ? 0 : 4, membership == "matched" ? "no-op" : "stale");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("None", "matched")]
    [InlineData("None", "remote-only")]
    [InlineData("Content", "matched")]
    [InlineData("Content", "remote-only")]
    public async Task G3_reference_resolution_candidates_bind_only_potential_binary_inputs(string itemType, string membership)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            $"<ItemGroup><Reference Include=\"ContractScribe.Roslyn\" /><{itemType} Include=\"../References/**/*\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        const string relative = "References/ContractScribe.Roslyn.dll";
        files[relative] = await File.ReadAllBytesAsync(typeof(RepositoryLoader).Assembly.Location);
        files["References/README.md"] = "Unrelated candidate-directory documentation.\n"u8.ToArray();
        if (membership == "matched")
        {
            Directory.CreateDirectory(Path.Join(root, "References"));
            await File.WriteAllBytesAsync(Path.Join(root, relative), files[relative]);
            var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(root, "Fixture.slnx"));
            Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
            await using var session = loaded.Session!;
            var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
            Assert.Contains(app.Project.MetadataReferences.OfType<Microsoft.CodeAnalysis.PortableExecutableReference>(),
                reference => reference.FilePath == Path.Join(root, relative));
        }
        AdvanceTarget(fixture, files, "Bind a compiler reference resolution candidate\n");
        await LimitBindingInvocation(fixture);
        AssertResult(await fixture.Run("start"), membership == "matched" ? 0 : 4, membership == "matched" ? "no-op" : "stale");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("disabled")]
    [InlineData("hint-first")]
    [InlineData("candidate-first")]
    [InlineData("excluded")]
    [InlineData("removed")]
    [InlineData("unrelated")]
    [InlineData("two-matched")]
    [InlineData("two-remote")]
    [InlineData("search-path")]
    [InlineData("extension")]
    [InlineData("extension-matched")]
    public async Task G3_reference_candidate_provenance_preserves_order_filters_and_ambiguity(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var bytes = await File.ReadAllBytesAsync(typeof(RepositoryLoader).Assembly.Location);
        Directory.CreateDirectory(Path.Join(root, "Hint"));
        const string name = "ContractScribe.Roslyn";
        var hint = "Hint/" + name + ".dll";
        await File.WriteAllBytesAsync(Path.Join(root, hint), bytes);
        var searches = mode == "disabled" ? "{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}"
            : mode == "hint-first" ? "{HintPathFromItem};{CandidateAssemblyFiles};{TargetFrameworkDirectory};{RawFileName}"
            : mode == "search-path" ? "../Candidates;{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}"
            : "{CandidateAssemblyFiles};{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}";
        var extension = mode.StartsWith("extension", StringComparison.Ordinal) ? ".candidate" : ".dll";
        var items = mode == "excluded" ? $"<None Include=\"../Candidates/**/*\" Exclude=\"../Candidates/**/{name}.dll\" />"
            : mode == "removed" ? $"<None Include=\"../Candidates/**/*\" /><None Remove=\"../Candidates/**/{name}.dll\" />"
            : "<None Include=\"../Candidates/**/*\" />";
        var projectPath = Path.Join(root, "App/App.csproj");
        var addition = $"<PropertyGroup><AssemblySearchPaths>{searches}</AssemblySearchPaths><AllowedReferenceAssemblyFileExtensions>{extension};.dll;.exe;.winmd</AllowedReferenceAssemblyFileExtensions></PropertyGroup>"
            + $"<ItemGroup><Reference Include=\"{name}\"><HintPath>../{hint}</HintPath></Reference>{items}</ItemGroup>";
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>", addition + "</Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        fixture.GovernedFiles[hint] = (bytes, null);
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        var candidate = "Candidates/" + (mode == "unrelated" ? "Other" : name) + extension;
        files[candidate] = bytes.Concat(new byte[] { 0 }).ToArray();
        files["Candidates/README.md"] = "Unrelated candidate-directory documentation.\n"u8.ToArray();
        if (mode is "two-matched" or "two-remote")
        {
            files.Remove(candidate);
            candidate = "Candidates/first/" + name + ".dll";
            files[candidate] = bytes;
            files["Candidates/second/" + name + ".dll"] = bytes.Concat(new byte[] { 0 }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(root, candidate))!);
            await File.WriteAllBytesAsync(Path.Join(root, candidate), bytes);
            if (mode == "two-matched")
            {
                var second = "Candidates/second/" + name + ".dll";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(root, second))!);
                await File.WriteAllBytesAsync(Path.Join(root, second), files[second]);
            }
        }
        if (mode == "extension-matched")
        {
            Directory.CreateDirectory(Path.Join(root, "Candidates"));
            await File.WriteAllBytesAsync(Path.Join(root, candidate), files[candidate]);
        }
        var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(root, "Fixture.slnx"));
        Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
        await using (var session = loaded.Session!)
        {
            var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
            Assert.Contains(app.Project.MetadataReferences.OfType<Microsoft.CodeAnalysis.PortableExecutableReference>(), reference =>
                mode is "two-matched" or "two-remote" or "extension-matched"
                    ? reference.FilePath!.StartsWith(Path.Join(root, "Candidates"), StringComparison.Ordinal)
                    : reference.FilePath == Path.Join(root, hint));
        }
        AdvanceTarget(fixture, files, "Bind only applicable reference-resolution inputs\n");
        await LimitBindingInvocation(fixture);
        var blocked = mode is "candidate-first" or "two-remote" or "search-path" or "extension";
        AssertResult(await fixture.Run("start"), blocked ? 4 : 0, blocked ? "stale" : "no-op");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("changed-arguments")]
    [InlineData("nested-ruleset")]
    public async Task G3_compiler_response_file_input_closure_cannot_be_omitted(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        var projectPath = Path.Join(root, "App/App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            "<PropertyGroup><CompilerResponseFile>../options.rsp</CompilerResponseFile></PropertyGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var response = mode == "changed-arguments" ? "/define:LOCAL_COMPILER_INPUT\n"
            : $"/ruleset:\"{Path.Join(root, "rules.ruleset")}\"\n";
        await File.WriteAllTextAsync(Path.Join(root, "options.rsp"), response);
        fixture.GovernedFiles["options.rsp"] = (Encoding.UTF8.GetBytes(response), null);
        if (mode == "nested-ruleset")
        {
            const string rules = "<RuleSet Name=\"Binding\" ToolsVersion=\"15.0\"><Rules AnalyzerId=\"Microsoft.CodeAnalysis.CSharp\" RuleNamespace=\"Microsoft.CodeAnalysis.CSharp\"><Rule Id=\"CS0168\" Action=\"None\" /></Rules></RuleSet>";
            await File.WriteAllTextAsync(Path.Join(root, "rules.ruleset"), rules);
            fixture.GovernedFiles["rules.ruleset"] = (Encoding.UTF8.GetBytes(rules), null);
            var sourcePath = Path.Join(root, "App/App.cs");
            var source = (await File.ReadAllTextAsync(sourcePath)).Replace("public static void Run() { }", "public static void Run() { int unused; }", StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, source);
            fixture.GovernedFiles["App/App.cs"] = (Encoding.UTF8.GetBytes(source), null);
        }
        var loaded = await new RepositoryLoader().LoadAsync(new RepositoryLoadRequest(root, "Fixture.slnx"));
        Assert.Equal(RepositoryLoadStatus.Success, loaded.Status);
        await using (var session = loaded.Session!)
        {
            var app = Assert.Single(session.Projects, project => project.ProjectIdentity == "App/App.csproj");
            if (mode == "changed-arguments")
                Assert.Contains("LOCAL_COMPILER_INPUT", ((Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)app.Project.ParseOptions!).PreprocessorSymbolNames);
            else Assert.Equal(Microsoft.CodeAnalysis.ReportDiagnostic.Suppress, app.Compilation.Options.SpecificDiagnosticOptions["CS0168"]);
        }
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        if (mode == "changed-arguments") files["options.rsp"] = "/define:REMOTE_COMPILER_INPUT\n"u8.ToArray();
        else files["rules.ruleset"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["rules.ruleset"]).Replace("Action=\"None\"", "Action=\"Error\"", StringComparison.Ordinal));
        AdvanceTarget(fixture, files, "Change actual compiler response input closure\n");
        await LimitBindingInvocation(fixture);
        AssertResult(await fixture.Run("start"), 4, "stale");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("matched")]
    [InlineData("quoted-comments")]
    [InlineData("utf16")]
    [InlineData("local-only")]
    [InlineData("remote-only")]
    [InlineData("unknown")]
    [InlineData("nested")]
    [InlineData("matched-reference")]
    public async Task G3_scalar_response_files_bind_bytes_and_preserve_ordinary_compiler_inputs(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Repository.Root;
        const string responsePath = "responses with spaces/options.rsp";
        var projectPath = Path.Join(root, "App/App.csproj");
        var addition = $"<PropertyGroup><CompilerResponseFile>../{responsePath}</CompilerResponseFile></PropertyGroup>";
        var contents = mode == "quoted-comments" ? "# comment @ignored\r\n\"/define:LOCAL_COMPILER_INPUT\"\n/d:\"SECOND_INPUT\" /nowarn:\"0168,0219\"\n/define:LAST_INPUT # trailing comment\n"
            : mode == "unknown" ? "/define:LOCAL_COMPILER_INPUT\n/future-file-option:input.json\n"
            : mode == "nested" ? $"/define:LOCAL_COMPILER_INPUT\n@\"{Path.Join(root, "nested.rsp")}\"\n"
            : "/define:LOCAL_COMPILER_INPUT\n";
        if (mode == "nested")
        {
            await File.WriteAllTextAsync(Path.Join(root, "nested.rsp"), "/define:NESTED_INPUT\n");
            fixture.GovernedFiles["nested.rsp"] = ("/define:NESTED_INPUT\n"u8.ToArray(), null);
        }
        if (mode == "matched-reference")
        {
            Directory.CreateDirectory(Path.Join(root, "References"));
            var bytes = await File.ReadAllBytesAsync(typeof(RepositoryLoader).Assembly.Location);
            await File.WriteAllBytesAsync(Path.Join(root, "References/Dependency.dll"), bytes);
            fixture.GovernedFiles["References/Dependency.dll"] = (bytes, null);
            addition += "<ItemGroup><Reference Include=\"Dependency\"><HintPath>../References/Dependency.dll</HintPath></Reference></ItemGroup>";
        }
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>", addition + "</Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), null);
        var response = mode == "utf16" ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(contents)).ToArray() : Encoding.UTF8.GetBytes(contents);
        if (mode != "remote-only")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(root, responsePath))!);
            await File.WriteAllBytesAsync(Path.Join(root, responsePath), response);
        }
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        if (mode != "local-only") files[responsePath] = response;
        AdvanceTarget(fixture, files, "Bind compiler response carrier and supported scalar arguments\n");
        await LimitBindingInvocation(fixture);
        var blocked = mode is "local-only" or "remote-only" or "unknown" or "nested";
        var result = await fixture.Run("start");
        AssertResult(result, blocked ? 4 : 0, blocked ? "stale" : "no-op");
        if (mode is "remote-only" or "unknown" or "nested")
            Assert.Contains("github-proposal.compiler-input-unverifiable", result.Stderr, StringComparison.Ordinal);
        if (mode == "local-only") Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    private static async Task LimitBindingInvocation(Fixture fixture)
    {
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
        await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
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
    [InlineData("obj", true)]
    [InlineData("bin", true)]
    [InlineData("obj", false)]
    [InlineData("bin", false)]
    public async Task G3_tracked_output_cannot_hide_declared_semantic_inputs_but_unrelated_outputs_are_excluded(string output, bool declared)
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectPath = Path.Join(fixture.Repository.Root, "App", "App.csproj");
        var project = (await File.ReadAllTextAsync(projectPath)).Replace("</Project>",
            $"<ItemGroup><AdditionalFiles Include=\"{output}/semantic-inputs/**/*.json\" /></ItemGroup></Project>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);
        fixture.GovernedFiles["App/App.csproj"] = (Encoding.UTF8.GetBytes(project), fixture.GovernedFiles["App/App.csproj"].Mode);
        var path = declared ? $"App/{output}/semantic-inputs/rules.json" : $"App/{output}/unrelated.json";
        Assert.False(File.Exists(Path.Join(fixture.Repository.Root, path)));
        var files = fixture.GovernedFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        files.Add(path, "{\"rule\":\"tracked-semantic-input\"}\n"u8.ToArray());
        fixture.Publication["expectedBaseCommitOid"] = AdvanceTarget(fixture, files, "Add tracked semantic input under output directory\n");
        if (!declared)
        {
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
            configuration["budgets"]!["invocation"]!["maximumTargets"] = 0;
            await File.WriteAllTextAsync(fixture.Configuration!, configuration.ToJsonString());
        }
        var result = await fixture.Run("start");
        AssertResult(result, declared ? 4 : 0, declared ? "stale" : "no-op");
        if (declared) Assert.Contains("github-proposal.checkout-mismatch", result.Stderr, StringComparison.Ordinal);
        Assert.True(Observed(fixture, "sdk-discovery") > 0);
        Assert.Equal(0, Observed(fixture, "provider-create"));
        Assert.Equal(0, Observed(fixture, "provider-send"));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
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
