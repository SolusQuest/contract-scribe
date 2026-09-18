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
    public async Task Applied_claim_readback_failure_reaches_the_real_CLI_as_a_closed_call_diagnostic()
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
        var result = await fixture.Run("start");
        AssertResult(result, 3, "conflict");
        using var json = JsonDocument.Parse(result.Stdout);
        var diagnostic = json.RootElement.GetProperty("publicationDiagnostic");
        Assert.Equal("CoordinationClaim", diagnostic.GetProperty("boundary").GetString());
        Assert.Equal("Coordination", diagnostic.GetProperty("owner").GetString());
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("transportCode").ValueKind);
        Assert.Equal("NotFound", diagnostic.GetProperty("recoveryCode").GetString());
        Assert.Equal(404, diagnostic.GetProperty("recoveryHttpStatus").GetInt32());
        Assert.Equal("claimed", fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Equal(1, fixture.GitHub.SuccessfulCas);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Fresh_process_reconstructs_and_charges_but_replays_the_original_publication_without_writes()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Run("start");
        AssertResult(first, 0, "published");
        var accepted = fixture.Checkpoint();
        var firstRemote = fixture.GitHub.Coordination().ToJsonString();
        var mutations = fixture.GitHub.Mutations;
        var second = await fixture.Run("resume");
        AssertResult(second, 0, "replayed");
        var reconstructed = fixture.Checkpoint();
        Assert.True(reconstructed.CheckpointRevision > accepted.CheckpointRevision);
        Assert.True(reconstructed.State.LineageCharges.PatchValidationInvocations
            > accepted.State.LineageCharges.PatchValidationInvocations);
        Assert.NotEqual(accepted.State.CandidateObservation!.PatchRequestSha256,
            reconstructed.State.CandidateObservation!.PatchRequestSha256);
        Assert.NotEqual(accepted.State.CandidateObservation.PatchResultCommitmentSha256,
            reconstructed.State.CandidateObservation.PatchResultCommitmentSha256);
        Assert.Equal(accepted.Sha256, reconstructed.State.AcceptedCandidateOrigin!.CheckpointSha256);
        Assert.Equal(accepted.CheckpointRevision, reconstructed.State.AcceptedCandidateOrigin.CheckpointRevision);
        Assert.Equal(firstRemote, fixture.GitHub.Coordination().ToJsonString());
        Assert.Equal(mutations, fixture.GitHub.Mutations);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.Equal(2, fixture.TokenReads());
        using var firstJson = JsonDocument.Parse(first.Stdout);
        Assert.Equal("https://github.com/Owner/repo/pull/1", firstJson.RootElement.GetProperty("pullRequestUrl").GetString());
        using var secondJson = JsonDocument.Parse(second.Stdout);
        Assert.Equal(reconstructed.CheckpointRevision, secondJson.RootElement.GetProperty("checkpointRevision").GetInt64());
        // R6's claim-only replay intentionally does not assert a PR URL.
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("pullRequestUrl").ValueKind);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData("ready", 0, "awaiting-review")]
    [InlineData("merged", 0, "merged")]
    [InlineData("closed", 4, "closed-unmerged")]
    public async Task Fresh_process_observes_lifecycle_without_changing_the_pull_request(string lifecycle, int exit, string outcome)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        fixture.GitHub.Lifecycle(lifecycle);
        var writes = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), exit, outcome);
        Assert.Equal(writes + 5, fixture.GitHub.Mutations); // One authenticated coordination observation.
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        var settledWrites = fixture.GitHub.Mutations;
        AssertResult(await fixture.Run("resume"), exit, outcome);
        Assert.Equal(settledWrites, fixture.GitHub.Mutations);
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
    public async Task Local_rejections_and_no_work_do_not_read_the_GitHub_credential(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(required: scenario != "no-work");
        if (scenario == "unknown-property")
        {
            fixture.Publication["token"] = "private-token-sentinel";
        }
        var arguments = fixture.Args("start");
        if (scenario == "duplicate-property")
        {
            var json = await File.ReadAllTextAsync(fixture.Request);
            await File.WriteAllTextAsync(fixture.Request, json[..^2] + ",\"operationId\":\"other\"}}");
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
        if (scenario == "missing-token" && OperatingSystem.IsLinux())
        {
            AssertResult(result, 4, "permission");
            Assert.Equal(1, fixture.TokenReads());
        }
        else
        {
            if (scenario == "no-work") AssertResult(result, 0, "no-op");
            else if (scenario == "missing-token") AssertResult(result, 5, "host-failure");
            else AssertResult(result, 4, "local-invalid");
            Assert.Equal(0, fixture.TokenReads());
        }
        Assert.Empty(fixture.GitHub.Requests);
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
        var result = await fixture.Run("start");
        AssertResult(result, 4, "local-invalid");
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public async Task Layer_drift_on_resume_rejects_as_incompatible_snapshot_without_remote_activity()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var checkpoint = await File.ReadAllBytesAsync(fixture.State);
        var providerRequests = fixture.Provider.RequestCount;
        var tokenReads = fixture.TokenReads();
        var githubRequests = fixture.GitHub.Requests.Count;
        var layer = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Configuration!))!;
        layer["provider"]!["model"] = "drifted-model";
        await File.WriteAllTextAsync(fixture.Configuration!, layer.ToJsonString(), new UTF8Encoding(false, true));
        var result = await fixture.Run("resume");
        AssertResult(result, 4, "stale");
        Assert.Equal("github proposal stopped before publication: campaign.incompatible-snapshot\n", result.Stderr);
        Assert.Equal(checkpoint, await File.ReadAllBytesAsync(fixture.State));
        Assert.Equal(providerRequests, fixture.Provider.RequestCount);
        Assert.Equal(tokenReads, fixture.TokenReads());
        Assert.Equal(githubRequests, fixture.GitHub.Requests.Count);
        await fixture.AssertSourceUnchanged();
    }

    [Fact]
    public async Task Resume_with_missing_state_is_local_invalid_without_remote_activity()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Run("resume");
        AssertResult(result, 4, "local-invalid");
        Assert.Equal("github proposal stopped before publication: campaign.state-missing\n", result.Stderr);
        Assert.False(File.Exists(fixture.State));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
    }

    [Fact]
    public async Task Defaults_only_resolution_stops_at_the_https_credential_boundary_without_dispatch()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration = null;
        var result = await fixture.Run("start");
        AssertResult(result, 4, "local-invalid");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("campaign", json.RootElement.GetProperty("terminalLayer").GetString());
        // Execution legitimately began: the checkpoint persisted its terminal
        // before the HTTPS credential boundary stopped provider dispatch.
        Assert.True(File.Exists(fixture.State));
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
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
        AssertResult(await fixture.Run("start"), 0, "published");
        Assert.Single(fixture.GitHub.PullRequests);
    }

    internal static void AssertResult(CampaignProcessResult result, int exit, string outcome)
    {
        Assert.True(result.ExitCode == exit, $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("github-proposal." + outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "githubProposalEnvelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion",
            "campaignOperation", "publicationOperationId", "generationId", "outcome", "diagnosticCodes",
            "checkpointRevision", "pullRequestUrl", "publicationDiagnostic" }, json.RootElement.EnumerateObject().Select(p => p.Name));
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
        internal string Snapshot = "snapshot.github";

        internal static async Task<Fixture> CreateAsync(bool required = true, bool twoWorks = false, bool newPath = false, bool rejectNewPath = false)
        {
            var repository = await LoaderFixture.CreateAsync();
            await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(repository.Root);
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
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var provider = new CampaignCliProcessTests.ProposalLoopbackServer();
            var source = await File.ReadAllBytesAsync(Path.Join(repository.Root, "App", "App.cs"));
            var governed = new Dictionary<string, (byte[] Bytes, UnixFileMode? Mode)>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(repository.Root, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(repository.Root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")
                    && (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal)
                        || Path.GetFileName(path) == "policy.json")))
                governed.Add(Path.GetRelativePath(repository.Root, path).Replace('\\', '/'),
                    (await File.ReadAllBytesAsync(path), OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : null));
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
                },
            }), new UTF8Encoding(false, true));
            var publication = new JsonObject
            {
                ["repositoryOwner"] = "Owner",
                ["repositoryName"] = "repo",
                ["targetRef"] = "refs/heads/main",
                ["expectedBaseCommitOid"] = github.BaseOid,
                ["operationId"] = "operation.initial",
                ["generationId"] = "generation.initial",
                ["policy"] = new JsonObject
                {
                    ["maximumDocumentationBlocks"] = 128,
                    ["maximumDistinctChangedFiles"] = 128,
                    ["maximumCumulativePatchBytes"] = 4194304,
                },
                ["transition"] = "initial",
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

        internal string[] Args(string operation, string? state = null, string? requestPath = null)
        {
            var request = new JsonObject
            {
                ["githubProposalRequestVersion"] = 1,
                ["campaignLineage"] = Lineage,
                ["snapshot"] = Snapshot,
                ["state"] = state ?? State,
                ["github"] = Publication.DeepClone(),
            };
            var path = requestPath ?? Request;
            File.WriteAllText(path, request.ToJsonString(), new UTF8Encoding(false));
            var arguments = new List<string>
            {
                "github-proposal", operation, "--repository-root", Repository.Root, "--input", "App/App.csproj",
                "--policy", "policy.json", "--request", path,
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
        internal async Task<CampaignProcessResult> Run(string operation, string? token = Placeholder, string? fault = null) =>
            await RunArgs(Args(operation), Environment(token, fault));

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
        internal CampaignCheckpointArtifact Checkpoint() => CampaignStateJson.Parse(File.ReadAllBytes(State)).Artifact!;
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
