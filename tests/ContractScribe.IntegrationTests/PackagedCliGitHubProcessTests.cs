using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

// Packed-payload GitHub publication matrix (issue #18 / M6-A1). Same
// record/verify harness as PackagedCliProcessTests: the environment selects
// the source-oracle DLL (record) or the installed D2 payload (verify), and the
// same scenario definitions produce the compared projections. The GitHub
// service is the wire-level loopback substitute; the token is the fixed
// synthetic placeholder — no real credential exists in either mode.
[Collection("Integration process lane 1")]
public sealed class PackagedCliGitHubProcessTests
{
    private const string Placeholder = "contract-scribe-synthetic-transport-only";
    private const string Lineage = "campaign.packed.github";
    private const string Snapshot = "snapshot.packed.github";

    [Fact]
    public async Task PackedGitHub_StartPublishAndReplay()
    {
        if (!PackagedCliProcessTests.Activated()) return;
        var fixture = await PackagedCliProcessTests.MaterializeFixtureAsync("github");
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-github");
        await using var provider = new CampaignCliProcessTests.ProposalLoopbackServer();
        var governed = await GovernedFilesAsync(fixture);
        await using var github = new GitHubProposalLoopbackServer(governed);
        var layer = Path.Join(outside, "layer.json");
        await WriteGitHubLayerAsync(layer, provider.Endpoint);
        var state = Path.Join(outside, "checkpoint.json");
        var request = Path.Join(outside, "request.json");
        var observations = Path.Join(outside, "observations.txt");
        var environment = GitHubEnvironment(github, observations, Placeholder);
        var projection = new JsonObject();

        var start = await RunGitHubStepAsync(
            "start", fixture, state, request, layer, github, provider, observations,
            environment, 0, "github-proposal.published");
        Assert.Single(github.PullRequests);
        Assert.Equal(1, github.PrAttempts);
        var mutationsAfterStart = github.Mutations;

        var resume = await RunGitHubStepAsync(
            "resume", fixture, state, request, layer, github, provider, observations,
            environment, 0, "github-proposal.replayed");
        Assert.Equal(mutationsAfterStart, github.Mutations);
        Assert.Single(github.PullRequests);
        Assert.Equal(1, github.PrAttempts);

        projection["start"] = start;
        projection["resume"] = resume;
        await PackagedCliProcessTests.AssertProjectionAsync("github-publish-replay", projection);
        await AssertSourceUnchangedAsync(fixture, governed);
    }

    [Fact]
    public async Task PackedGitHub_MissingTokenStopsAtCredentialBoundary()
    {
        if (!PackagedCliProcessTests.Activated()) return;
        var fixture = await PackagedCliProcessTests.MaterializeFixtureAsync("github-token");
        var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-packed-github-token");
        await using var provider = new CampaignCliProcessTests.ProposalLoopbackServer();
        var governed = await GovernedFilesAsync(fixture);
        await using var github = new GitHubProposalLoopbackServer(governed);
        var layer = Path.Join(outside, "layer.json");
        await WriteGitHubLayerAsync(layer, provider.Endpoint);
        var state = Path.Join(outside, "checkpoint.json");
        var request = Path.Join(outside, "request.json");
        var observations = Path.Join(outside, "observations.txt");
        var environment = GitHubEnvironment(github, observations, null);

        var projection = new JsonObject
        {
            ["start"] = await RunGitHubStepAsync(
                "start", fixture, state, request, layer, github, provider, observations,
                environment, 4, "github-proposal.permission"),
        };
        Assert.Empty(github.Requests);
        await PackagedCliProcessTests.AssertProjectionAsync("github-missing-token", projection);
    }

    private static async Task<JsonObject> RunGitHubStepAsync(
        string operation,
        string fixtureRoot,
        string statePath,
        string requestPath,
        string layer,
        GitHubProposalLoopbackServer github,
        CampaignCliProcessTests.ProposalLoopbackServer provider,
        string observations,
        IReadOnlyDictionary<string, string?> environment,
        int expectedExit,
        string expectedOutcome)
    {
        var request = new JsonObject
        {
            ["githubProposalRequestVersion"] = 1,
            ["campaignLineage"] = Lineage,
            ["snapshot"] = Snapshot,
            ["state"] = statePath,
            ["github"] = new JsonObject
            {
                ["repositoryOwner"] = "Owner",
                ["repositoryName"] = "repo",
                ["targetRef"] = "refs/heads/main",
                ["expectedBaseCommitOid"] = github.BaseOid,
                ["operationId"] = "operation.packed",
                ["generationId"] = "generation.packed",
                ["policy"] = new JsonObject
                {
                    ["maximumDocumentationBlocks"] = 128,
                    ["maximumDistinctChangedFiles"] = 128,
                    ["maximumCumulativePatchBytes"] = 4194304,
                },
                ["transition"] = "initial",
            },
        };
        await File.WriteAllTextAsync(requestPath, request.ToJsonString(), new UTF8Encoding(false, true));
        var result = await PackagedCliProcessTests.RunCliAsync(
            [
                "github-proposal", operation,
                "--repository-root", fixtureRoot,
                "--input", "App/App.csproj",
                "--policy", "policy.json",
                "--request", requestPath,
                "--configuration", layer,
            ],
            environment);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var outcome = root.GetProperty("outcome").GetString();
        Assert.True(
            result.ExitCode == expectedExit && outcome == expectedOutcome,
            $"{operation}: exit={result.ExitCode} outcome={outcome} "
            + $"stdout={result.Stdout} stderr={result.Stderr}");
        Assert.DoesNotContain(Placeholder, result.Stdout + result.Stderr);
        return GitHubProjection(operation, result, github, provider, statePath, observations);
    }

    private static JsonObject GitHubProjection(
        string operation,
        PackagedCliProcessTests.ProcessResult result,
        GitHubProposalLoopbackServer github,
        CampaignCliProcessTests.ProposalLoopbackServer provider,
        string statePath,
        string observations)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var pullRequestUrl = root.GetProperty("pullRequestUrl");
        return new JsonObject
        {
            ["operation"] = operation,
            ["exitCode"] = result.ExitCode,
            ["terminalLayer"] = root.GetProperty("terminalLayer").GetString(),
            ["outcome"] = root.GetProperty("outcome").GetString(),
            ["diagnosticCodes"] = new JsonArray(root.GetProperty("diagnosticCodes")
                .EnumerateArray()
                .Select(e => (JsonNode)JsonValue.Create(e.GetString())!)
                .ToArray()),
            ["checkpointRevision"] =
                root.GetProperty("checkpointRevision").ValueKind == JsonValueKind.Number
                    ? JsonValue.Create(root.GetProperty("checkpointRevision").GetInt64())
                    : null,
            ["pullRequestUrlPath"] = pullRequestUrl.ValueKind == JsonValueKind.String
                ? JsonValue.Create(new Uri(pullRequestUrl.GetString()!).AbsolutePath)
                : null,
            ["checkpointValid"] = File.Exists(statePath)
                && ContractScribe.Core.CampaignStateJson.Parse(
                    File.ReadAllBytes(statePath)).IsValid,
            ["githubMutations"] = github.Mutations,
            ["githubRequests"] = github.Requests.Count,
            ["providerRequests"] = provider.RequestCount,
            ["tokenReads"] = File.Exists(observations)
                ? File.ReadAllLines(observations).Count(line => line == "token-read")
                : 0,
        };
    }

    private static Dictionary<string, string?> GitHubEnvironment(
        GitHubProposalLoopbackServer github, string observations, string? token) => new()
        {
            ["DOTNET_STARTUP_HOOKS"] = PackagedCliProcessTests.HookPath,
            ["CONTRACTSCRIBE_TEST_GITHUB_ENDPOINT"] = github.Endpoint.AbsoluteUri,
            ["CONTRACTSCRIBE_GITHUB_TOKEN"] = token,
            ["CONTRACTSCRIBE_TEST_GITHUB_OBSERVATIONS"] = observations,
        };

    private static async Task WriteGitHubLayerAsync(string destination, Uri endpoint)
    {
        var layer = new JsonObject
        {
            ["consumerConfigurationVersion"] = 1,
            ["provider"] = new JsonObject
            {
                ["endpoint"] = endpoint.AbsoluteUri,
                ["model"] = "fixture-model",
                ["requestProfile"] = new JsonObject { ["toolChoice"] = "auto" },
            },
            ["budgets"] = new JsonObject
            {
                ["campaign"] = new JsonObject
                {
                    ["maximumCandidatesPerBlock"] = 30,
                    ["maximumElapsedMilliseconds"] = 3_600_000,
                },
            },
        };
        await File.WriteAllTextAsync(destination, layer.ToJsonString(), new UTF8Encoding(false, true));
    }

    private static async Task<Dictionary<string, byte[]>> GovernedFilesAsync(string fixtureRoot)
    {
        var governed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(fixtureRoot, path)
                    .Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")
                && (path.EndsWith(".cs", StringComparison.Ordinal)
                    || path.EndsWith(".csproj", StringComparison.Ordinal)
                    || Path.GetFileName(path) == "policy.json")))
        {
            governed[Path.GetRelativePath(fixtureRoot, path).Replace('\\', '/')] =
                await File.ReadAllBytesAsync(path);
        }
        return governed;
    }

    private static async Task AssertSourceUnchangedAsync(
        string fixtureRoot, Dictionary<string, byte[]> governed)
    {
        foreach (var pair in governed)
        {
            Assert.Equal(
                pair.Value,
                await File.ReadAllBytesAsync(Path.Join(fixtureRoot, pair.Key)));
        }
    }
}
