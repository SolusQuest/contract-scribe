using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using CampaignProcessResult = ContractScribe.Roslyn.IntegrationTests.CampaignCliProcessTests.ProcessResult;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Ref_checkpoint_survives_a_new_CLI_process_without_a_local_checkpoint_file()
    {
        await using var fixture = await Fixture.CreateAsync();
        var started = await fixture.Run();
        AssertResult(started, 0, "published");
        using (var measured = JsonDocument.Parse(started.Stdout))
        {
            var usage = measured.RootElement.GetProperty("invocationUsage");
            Assert.Equal(fixture.Provider.RequestCount, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
            Assert.Equal(2, usage.GetProperty("calls").GetProperty("logicalProviderCalls").GetInt32());
            Assert.Equal(0, usage.GetProperty("calls").GetProperty("retryDispatches").GetInt32());
            Assert.Equal(31, usage.GetProperty("tokens").GetProperty("input").GetProperty("observed").GetInt64());
            Assert.Equal(29, usage.GetProperty("tokens").GetProperty("cachedInput").GetProperty("observed").GetInt64());
            Assert.Equal(2, usage.GetProperty("tokens").GetProperty("uncachedInput").GetProperty("observed").GetInt64());
            Assert.Equal(1, usage.GetProperty("work").GetProperty("startedTargets").GetInt32());
            Assert.Equal(1, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
        }
        Assert.False(File.Exists(fixture.State));
        var before = fixture.Checkpoint();
        var resumed = await fixture.Run();
        AssertResult(resumed, 0, "awaiting-review");
        using (var measured = JsonDocument.Parse(resumed.Stdout))
        {
            var usage = measured.RootElement.GetProperty("invocationUsage");
            Assert.Equal(0, usage.GetProperty("calls").GetProperty("physicalProviderDispatches").GetInt32());
            Assert.Equal(0, usage.GetProperty("tokens").GetProperty("input").GetProperty("observed").GetInt64());
            Assert.Equal(0, usage.GetProperty("work").GetProperty("startedTargets").GetInt32());
            Assert.Equal(0, usage.GetProperty("work").GetProperty("acceptedTargets").GetInt32());
            Assert.Equal("awaiting-review", usage.GetProperty("termination").GetProperty("stopReason").GetString());
            Assert.Equal(2, usage.GetProperty("history").GetProperty("providerRequests").GetProperty("observed").GetInt64());
        }
        Assert.False(File.Exists(fixture.State));
        Assert.Equal(before.Sha256, fixture.Checkpoint().Sha256);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Applied_checkpoint_claim_readback_failure_stops_before_provider_or_publication()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.After = request =>
        {
            if (request.Method == "POST" && request.Path == "/graphql")
            {
                fixture.GitHub.RejectPath = "/git/ref/heads/contract-scribe/coordination/";
                fixture.GitHub.RejectionStatus = 404;
            }
        };
        var result = await fixture.Run();
        AssertResult(result, 3, "conflict");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("preflight", json.RootElement.GetProperty("terminalLayer").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publicationDiagnostic").ValueKind);
        var checkpointFailure = json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal("Acquire", checkpointFailure.GetProperty("operation").GetString());
        Assert.Equal("FinalRef", checkpointFailure.GetProperty("stage").GetString());
        Assert.Equal("RefMismatch", checkpointFailure.GetProperty("reason").GetString());
        Assert.Equal("NotFound", checkpointFailure.GetProperty("transportCode").GetString());
        Assert.Equal(404, checkpointFailure.GetProperty("httpStatus").GetInt32());
        Assert.Null(fixture.GitHub.CoordinationDocument()["publication"]);
        Assert.Null(fixture.GitHub.CoordinationDocument()["checkpoint"]);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Equal(1, fixture.GitHub.SuccessfulCas);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Published_resume_waits_without_reconstruction_charges_or_publication_writes()
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run(), 0, "published");
        var accepted = fixture.Checkpoint();
        var before = GateCounts(fixture);
        var writes = fixture.GitHub.Mutations;
        var result = await fixture.Run();
        AssertResult(result, 0, "awaiting-review");
        Assert.Equal(accepted.Sha256, fixture.Checkpoint().Sha256);
        Assert.Equal(before, GateCounts(fixture));
        Assert.Equal(writes, fixture.GitHub.Mutations);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(accepted.CheckpointRevision, json.RootElement.GetProperty("checkpointRevision").GetInt64());
        Assert.Equal("https://github.com/Owner/repo/pull/1", json.RootElement.GetProperty("pullRequestUrl").GetString());
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData("ready", 0, "awaiting-review")]
    [InlineData("merged", 0, "no-op")]
    [InlineData("closed", 4, "closed-unmerged")]
    public async Task Fresh_process_observes_lifecycle_without_changing_the_pull_request(string lifecycle, int exit, string outcome)
    {
        if (!OperatingSystem.IsLinux() && !ContractScribe.Roslyn.DarwinFileSystem.IsSupported) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run(), 0, "published");
        fixture.GitHub.Lifecycle(lifecycle);
        if (lifecycle == "merged")
        {
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
            layer["budgets"]!["invocation"]!["maximumTargets"] = 0;
            await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString());
        }
        var writes = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run(), exit, outcome);
        Assert.Equal(writes, fixture.GitHub.PublicationWrites);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var settledWrites = fixture.GitHub.PublicationWrites;
        AssertResult(await fixture.Run(), exit, outcome);
        Assert.Equal(settledWrites, fixture.GitHub.PublicationWrites);
        Assert.Single(fixture.GitHub.PullRequests);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("request-token")]
    [InlineData("request-corrupt")]
    [InlineData("missing-token")]
    [InlineData("no-work")]
    public async Task Local_rejections_precede_credentials_but_remote_no_work_requires_a_claim(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(required: scenario != "no-work");
        if (scenario == "unknown-property")
        {
            fixture.Publication["token"] = "private-token-sentinel";
        }
        var arguments = fixture.Args();
        if (scenario == "duplicate-property")
        {
            var json = await File.ReadAllTextAsync(fixture.Request);
            await File.WriteAllTextAsync(fixture.Request, json[..^2] + ",\"targetRef\":\"refs/heads/other\"}}");
        }
        if (scenario == "request-token")
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Request))!;
            json["token"] = "private-token-sentinel";
            await File.WriteAllTextAsync(fixture.Request, json.ToJsonString());
        }
        if (scenario == "request-corrupt")
        {
            await File.WriteAllTextAsync(fixture.Request, "{");
        }
        var result = await fixture.RunArgs(arguments,
            fixture.Environment(token: scenario == "missing-token" ? null : Fixture.Placeholder));
        if (scenario == "missing-token")
        {
            AssertResult(result, 4, "permission");
            Assert.Equal(1, fixture.TokenReads());
            Assert.Empty(fixture.GitHub.Requests);
        }
        else if (scenario == "no-work")
        {
            AssertResult(result, 0, "no-op");
            Assert.Equal(1, fixture.TokenReads());
            Assert.NotEmpty(fixture.GitHub.Requests);
            Assert.Equal(0, fixture.GitHub.PublicationWrites);
        }
        else
        {
            AssertResult(result, 4, "local-invalid");
            Assert.Equal(0, fixture.TokenReads());
            Assert.Empty(fixture.GitHub.Requests);
        }
        Assert.DoesNotContain("private-token-sentinel", result.Stdout + result.Stderr);
    }

    [Theory]
    [InlineData("campaignLineage")]
    [InlineData("productContractRevisionSha256")]
    public async Task Configuration_layers_cannot_smuggle_invocation_or_product_authority(string field)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Configuration!,
            "{\"consumerConfigurationVersion\":1,"
            + "\"provider\":{\"endpoint\":\"https://provider.invalid/v1/chat/completions\"},"
            + "\"planning\":{\"" + field + "\":\"smuggled.value\"}}\n", new UTF8Encoding(false, true));
        var result = await fixture.Run();
        AssertResult(result, 4, "local-invalid");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public async Task Layer_drift_on_resume_rejects_without_provider_or_publication_activity()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run(), 0, "published");
        var checkpoint = fixture.GitHub.CheckpointBytes();
        var providerRequests = fixture.Provider.RequestCount;
        var tokenReads = fixture.TokenReads();
        var githubRequests = fixture.GitHub.Requests.Count;
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["provider"]!["model"] = "drifted-model";
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString(), new UTF8Encoding(false, true));
        var result = await fixture.Run();
        AssertResult(result, 0, "awaiting-review");
        Assert.Empty(result.Stderr);
        Assert.Equal(checkpoint, fixture.GitHub.CheckpointBytes());
        Assert.Equal(providerRequests, fixture.Provider.RequestCount);
        Assert.Equal(tokenReads + 1, fixture.TokenReads());
        Assert.True(fixture.GitHub.Requests.Count > githubRequests);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Automatic_first_invocation_starts_only_after_observing_an_empty_remote_campaign()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.DoesNotContain(fixture.GitHub.Refs.Keys,
            reference => reference.Contains("/coordination/", StringComparison.Ordinal));
        var result = await fixture.Run();
        AssertResult(result, 0, "published");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("start", json.RootElement.GetProperty("campaignOperation").GetString());
        Assert.Empty(result.Stderr);
        Assert.False(File.Exists(fixture.State));
        Assert.True(fixture.Provider.RequestCount > 0);
        Assert.Equal(1, fixture.TokenReads());
        Assert.NotEmpty(fixture.GitHub.Requests);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.NotNull(fixture.Checkpoint().State.CandidateObservation);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Defaults_only_resolution_stops_at_the_https_credential_boundary_without_dispatch()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration = null;
        var result = await fixture.Run();
        AssertResult(result, 4, "local-invalid");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("campaign", json.RootElement.GetProperty("terminalLayer").GetString());
        // Execution legitimately began: the checkpoint persisted its terminal
        // before the HTTPS credential boundary stopped provider dispatch.
        Assert.False(File.Exists(fixture.State));
        Assert.NotNull(fixture.Checkpoint());
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(1, fixture.TokenReads());
        Assert.NotEmpty(fixture.GitHub.Requests);
        Assert.Equal(0, fixture.GitHub.PublicationWrites);
    }

    [Fact]
    public async Task Invocation_override_wins_over_the_consumer_layer_through_the_same_resolver()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        var wrong = Path.Join(fixture.Outside, "layer-wrong.json");
        await File.WriteAllTextAsync(wrong,
            "{\"consumerConfigurationVersion\":1,\"provider\":{\"endpoint\":\"http://127.0.0.1:1/v1/chat/completions\"}}\n",
            new UTF8Encoding(false, true));
        fixture.Configuration = wrong;
        fixture.ConfigurationOverride = Path.Join(fixture.Outside, "layer-override.json");
        await CampaignCliProcessTests.WriteConsumerLayerAsync(fixture.ConfigurationOverride, fixture.Provider.Endpoint);
        AssertResult(await fixture.Run(), 0, "published");
        Assert.Single(fixture.GitHub.PullRequests);
    }

    internal static void AssertResult(CampaignProcessResult result, int exit, string outcome)
    {
        Assert.True(result.ExitCode == exit, $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("github-proposal." + outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "githubProposalEnvelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion",
            "campaignOperation", "publicationOperationId", "generationId", "outcome", "diagnosticCodes",
            "checkpointRevision", "pullRequestUrl", "publicationDiagnostic", "invocationUsage" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.EndsWith("}\n", result.Stdout);
        Assert.DoesNotContain('\r', result.Stdout);
        if (exit == 0)
        {
            Assert.Empty(result.Stderr);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publicationDiagnostic").ValueKind);
        }
        else Assert.Single(result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain(Fixture.Placeholder, result.Stdout + result.Stderr);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal const string Placeholder = "contract-scribe-synthetic-transport-only";
        internal const string Lineage = "campaign.github";
        internal required LoaderFixture Repository;
        internal required CampaignCliProcessTests.ProposalLoopbackServer Provider;
        internal required GitHubProposalLoopbackServer GitHub;
        internal required string Outside, State, Request, Observations;
        internal required JsonObject Publication;
        internal string? Configuration;
        internal string? ConfigurationOverride;
        internal required byte[] Source;
        internal required Dictionary<string, (byte[] Bytes, UnixFileMode? Mode)> GovernedFiles;

        internal static async Task<Fixture> CreateAsync(bool required = true, bool twoWorks = false, bool newPath = false, bool rejectNewPath = false,
            bool unsupportedProperty = false, int targetCount = 0, string providerScenario = "accepted", bool includeUsage = true)
        {
            var repository = await LoaderFixture.CreateAsync();
            await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(repository.Root);
            if (targetCount > 0)
            {
                var path = Path.Join(repository.Root, "App", "App.cs");
                var text = await File.ReadAllTextAsync(path);
                await File.WriteAllTextAsync(path, text.Replace("    public static void Run() { }",
                    string.Join("\n", Enumerable.Range(0, targetCount).Select(index => $"    public static void Method{index:D2}() {{ }}")), StringComparison.Ordinal));
            }
            if (unsupportedProperty)
            {
                var path = Path.Join(repository.Root, "App", "App.cs");
                var sourceText = await File.ReadAllTextAsync(path);
                await File.WriteAllTextAsync(path, sourceText.Replace("    public static void Run() { }",
                    "    public static void Run() { }\n    public static int Value { get; set; }", StringComparison.Ordinal));
            }
            if (twoWorks)
            {
                var path = Path.Join(repository.Root, "App", "App.cs");
                var sourceText = await File.ReadAllTextAsync(path);
                if (newPath)
                    await File.WriteAllTextAsync(Path.Join(repository.Root, "App", "Other.cs"),
                        sourceText.Replace("class App", "class Other", StringComparison.Ordinal)
                            .Replace("namespace Fixture;\n", rejectNewPath ? "namespace Fixture;\r\n" : "namespace Fixture;\n", StringComparison.Ordinal));
                else
                    await File.WriteAllTextAsync(path, sourceText.Replace("    public static void Run() { }", "    public static void Run() { }\n    public static void Other() { }", StringComparison.Ordinal));
            }
            await File.WriteAllTextAsync(Path.Join(repository.Root, "policy.json"),
                required ? CampaignCliProcessTests.RequiredPolicy : CampaignCliProcessTests.OptionalPolicy);
            var outside = Path.Join(Path.GetTempPath(), "contract-scribe-github-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            if (OperatingSystem.IsLinux() || ContractScribe.Roslyn.DarwinFileSystem.IsSupported) File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var provider = new CampaignCliProcessTests.ProposalLoopbackServer(providerScenario, includeUsage: includeUsage);
            var source = await File.ReadAllBytesAsync(Path.Join(repository.Root, "App", "App.cs"));
            var governed = new Dictionary<string, (byte[] Bytes, UnixFileMode? Mode)>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(repository.Root, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(repository.Root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin" or ".git")))
                governed.Add(Path.GetRelativePath(repository.Root, path).Replace('\\', '/'),
                    (await File.ReadAllBytesAsync(path), (OperatingSystem.IsLinux() || ContractScribe.Roslyn.DarwinFileSystem.IsSupported) ? File.GetUnixFileMode(path) : null));
            var github = new GitHubProposalLoopbackServer(governed.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes));
            var configuration = Path.Join(outside, "layer.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
            {
                consumerConfigurationVersion = 1,
                provider = new
                {
                    endpoint = provider.Endpoint.AbsoluteUri,
                    model = "fixture-model",
                    requestProfile = new { toolChoice = "auto" },
                },
                budgets = new
                {
                    campaign = new { maximumCandidatesPerBlock = 30, maximumElapsedMilliseconds = 3_600_000 },
                    // Keep both original members, but publish only the first durable
                    // proposal; publication seals that subset permanently.
                    invocation = new { maximumProviderRequests = twoWorks ? 1 : 64 },
                },
            }), new UTF8Encoding(false, true));
            var publication = new JsonObject
            {
                ["repositoryOwner"] = "Owner",
                ["repositoryName"] = "repo",
                ["targetRef"] = "refs/heads/main",
            };
            return new Fixture
            {
                Repository = repository,
                Provider = provider,
                GitHub = github,
                Outside = outside,
                State = Path.Join(outside, "checkpoint.json"),
                Request = Path.Join(outside, "request.json"),
                Publication = publication,
                Configuration = configuration,
                Observations = Path.Join(outside, "observations.txt"),
                Source = source,
                GovernedFiles = governed,
            };
        }

        internal string[] Args(string? requestPath = null)
        {
            var request = new JsonObject
            {
                ["githubCampaignVersion"] = 1,
                ["campaignLineage"] = Lineage,
                ["github"] = Publication.DeepClone(),
            };
            var path = requestPath ?? Request;
            File.WriteAllText(path, request.ToJsonString(), new UTF8Encoding(false));
            var arguments = new List<string>
            {
                "github-proposal", "--repository-root", Repository.Root, "--input", "App/App.csproj",
                "--policy", "policy.json", "--campaign", path,
            };
            if (Configuration is not null) arguments.AddRange(["--configuration", Configuration]);
            if (ConfigurationOverride is not null) arguments.AddRange(["--configuration-override", ConfigurationOverride]);
            return arguments.ToArray();
        }
        internal Dictionary<string, string?> Environment(string? token = Placeholder, string? fault = null) => new()
        {
            ["DOTNET_STARTUP_HOOKS"] = CampaignCliProcessTests.StartupHookPath,
            ["CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT"] = GitHub.Endpoint.AbsoluteUri,
            ["CONTRACTSCRIBE_GITHUB_TOKEN"] = token,
            ["CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS"] = Observations,
            ["CONTRACTSCRIBE_TEST_GITHUB_FAULT"] = fault,
        };
        internal async Task<CampaignProcessResult> Run(string? token = Placeholder, string? fault = null) =>
            await RunArgs(Args(), Environment(token, fault));

        internal async Task<CampaignProcessResult> RunArgs(string[] arguments, Dictionary<string, string?> environment)
        {
            using var process = CampaignCliProcessTests.Start(arguments, environment);
            try
            {
                await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                return await process.CompleteAsync();
            }
            finally { await CampaignCliProcessTests.StopAsync(process); }
        }
        internal CampaignCheckpointArtifact Checkpoint() => CampaignStateJson.Parse(GitHub.CheckpointBytes()).Artifact!;
        internal int TokenReads() => File.Exists(Observations) ? File.ReadAllLines(Observations).Count(line => line == "token-read") : 0;
        internal async Task AssertSourceUnchanged()
        {
            foreach (var pair in GovernedFiles)
            {
                var path = Path.Join(Repository.Root, pair.Key);
                Assert.Equal(pair.Value.Bytes, await File.ReadAllBytesAsync(path));
                if (OperatingSystem.IsLinux()) Assert.Equal(pair.Value.Mode, File.GetUnixFileMode(path));
            }
        }
        public async ValueTask DisposeAsync()
        {
            await GitHub.DisposeAsync();
            await Provider.DisposeAsync();
            await Repository.DisposeAsync();
            Directory.Delete(Outside, recursive: true);
        }
    }
}
