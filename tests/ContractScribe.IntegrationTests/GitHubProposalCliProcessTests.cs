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
    [InlineData("missing-token")]
    [InlineData("no-work")]
    public async Task Local_rejections_and_no_work_do_not_read_the_GitHub_credential(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(required: scenario != "no-work");
        if (scenario == "unknown-property")
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.GitHubConfiguration))!;
            json["token"] = "private-token-sentinel";
            await File.WriteAllTextAsync(fixture.GitHubConfiguration, json.ToJsonString());
        }
        if (scenario == "duplicate-property")
        {
            var json = await File.ReadAllTextAsync(fixture.GitHubConfiguration);
            await File.WriteAllTextAsync(fixture.GitHubConfiguration, json[..^1] + ",\"operationId\":\"other\"}");
        }
        var result = await fixture.Run("start", token: scenario == "missing-token" ? null : Fixture.Placeholder);
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

    internal static void AssertResult(CampaignProcessResult result, int exit, string outcome)
    {
        Assert.True(result.ExitCode == exit, $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("github-proposal." + outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "githubProposalEnvelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion",
            "campaignOperation", "publicationOperationId", "generationId", "outcome", "diagnosticCodes",
            "checkpointRevision", "pullRequestUrl" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.EndsWith("}\n", result.Stdout);
        Assert.DoesNotContain('\r', result.Stdout);
        if (exit == 0) Assert.Empty(result.Stderr);
        else Assert.Single(result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain(Fixture.Placeholder, result.Stdout + result.Stderr);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal const string Placeholder = "contract-scribe-synthetic-transport-only";
        internal required LoaderFixture Repository;
        internal required CampaignCliProcessTests.ProposalLoopbackServer Provider;
        internal required GitHubProposalLoopbackServer GitHub;
        internal required string Outside, State, Configuration, GitHubConfiguration, Observations;
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
            var configuration = Path.Join(outside, "campaign.json");
            await CampaignCliProcessTests.WriteConfigurationAsync(configuration, provider.Endpoint);
            var campaign = JsonNode.Parse(await File.ReadAllTextAsync(configuration))!;
            campaign["budgets"]!["campaign"]!["maximumCandidatesPerBlock"] = 30;
            campaign["budgets"]!["campaign"]!["maximumElapsedMilliseconds"] = 3_600_000;
            await File.WriteAllTextAsync(configuration, campaign.ToJsonString());
            var githubConfiguration = Path.Join(outside, "github.json");
            await File.WriteAllTextAsync(githubConfiguration, JsonSerializer.Serialize(new
            {
                repositoryOwner = "Owner",
                repositoryName = "repo",
                targetRef = "refs/heads/main",
                expectedBaseCommitOid = github.BaseOid,
                operationId = "operation.initial",
                generationId = "generation.initial",
                policy = new { maximumDocumentationBlocks = 128, maximumDistinctChangedFiles = 128, maximumCumulativePatchBytes = 4194304 },
                transition = "initial",
            }));
            return new Fixture
            {
                Repository = repository,
                Provider = provider,
                GitHub = github,
                Outside = outside,
                State = Path.Join(outside, "checkpoint.json"),
                Configuration = configuration,
                GitHubConfiguration = githubConfiguration,
                Observations = Path.Join(outside, "observations.txt"),
                Source = source,
                GovernedFiles = governed,
            };
        }

        internal string[] Args(string operation, string? state = null) =>
        [
            "github-proposal", operation, "--repository-root", Repository.Root, "--input", "App/App.csproj",
            "--policy", "policy.json", "--snapshot", Snapshot, "--state", state ?? State,
            "--configuration", Configuration, "--github-configuration", GitHubConfiguration,
        ];
        internal Dictionary<string, string?> Environment(string? token = Placeholder, string? fault = null) => new()
        {
            ["DOTNET_STARTUP_HOOKS"] = CampaignCliProcessTests.StartupHookPath,
            ["CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT"] = GitHub.Endpoint.AbsoluteUri,
            ["CONTRACTSCRIBE_GITHUB_TOKEN"] = token,
            ["CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS"] = Observations,
            ["CONTRACTSCRIBE_TEST_GITHUB_FAULT"] = fault,
        };
        internal async Task<CampaignProcessResult> Run(string operation, string? token = Placeholder, string? fault = null)
        {
            using var process = CampaignCliProcessTests.Start(Args(operation), Environment(token, fault));
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
