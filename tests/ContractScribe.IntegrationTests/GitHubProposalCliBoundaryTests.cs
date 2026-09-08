using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [Fact]
    public async Task Ordinary_environment_cannot_activate_startup_controls_and_help_matches_its_fixture()
    {
        await using var fixture = await Fixture.CreateAsync(required: false);
        var environment = fixture.Environment();
        environment["DOTNET_STARTUP_HOOKS"] = null;
        environment["CONTRACTSCRIBE_TEST_GITHUB_TERMINAL"] = "host-contract-error";
        environment["CONTRACTSCRIBE_TEST_GITHUB_FAULT"] = "before-presentation";
        using var help = CampaignCliProcessTests.Start(["github-proposal", "--help"], environment);
        try
        {
            await help.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
            var result = await help.CompleteAsync();
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stderr);
            Assert.Equal(await File.ReadAllTextAsync(Path.Join(CampaignCliProcessTests.RepositoryRoot,
                "tests", "fixtures", "github-proposal", "cli", "help.txt")), result.Stdout);
        }
        finally { await CampaignCliProcessTests.StopAsync(help); }
        using var run = CampaignCliProcessTests.Start(fixture.Args("start"), environment);
        try
        {
            await run.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            AssertResult(await run.CompleteAsync(), 0, "no-op");
            Assert.Empty(fixture.GitHub.Requests);
            Assert.False(File.Exists(fixture.Observations));
        }
        finally { await CampaignCliProcessTests.StopAsync(run); }
    }

    [Theory]
    [InlineData("before-presentation")]
    [InlineData("after-terminal")]
    public async Task Selected_publication_survives_real_process_presentation_and_host_teardown_faults(string boundary)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start", fault: boundary), 0, "published");
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.TokenReads());
    }

    [Fact]
    public async Task Pre_emission_failure_has_one_sanitized_fallback_without_secret_access()
    {
        await using var fixture = await Fixture.CreateAsync(required: false);
        var result = await fixture.Run("start", fault: "before-presentation");
        AssertResult(result, 5, "host-failure");
        Assert.Equal("github proposal stopped before publication: github-proposal.campaign-contract-error\n", result.Stderr);
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
    }

    [Theory]
    [InlineData("title", 4, "human-change")]
    [InlineData("body", 4, "human-change")]
    [InlineData("head", 4, "human-change")]
    [InlineData("original-bytes", 4, "human-change")]
    [InlineData("tree", 4, "human-change")]
    [InlineData("mode", 4, "human-change")]
    [InlineData("duplicate", 3, "conflict")]
    [InlineData("permission", 4, "permission")]
    [InlineData("rate-limit", 3, "rate-limit")]
    [InlineData("incomplete-pagination", 5, "host-failure")]
    [InlineData("corrupt-coordination", 4, "human-change")]
    public async Task Fresh_process_authenticates_remote_changes_and_never_repairs_them(string kind, int exit, string outcome)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        AssertResult(await fixture.Run("start"), 0, "published");
        var server = fixture.GitHub;
        lock (server.Gate)
        {
            if (kind is "title" or "body") server.PullRequests[0][kind] = "human change";
            if (kind == "incomplete-pagination") server.IncompletePagination = true;
            if (kind == "corrupt-coordination")
            {
                var reference = server.Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal));
                var tree = server.Commits[reference.Value]["tree"]!["sha"]!.GetValue<string>();
                var leaf = Assert.Single(server.Trees[tree]).Oid;
                var blob = Assert.Single(server.Trees[leaf]).Oid;
                Assert.True(server.Blobs.ContainsKey(blob));
                server.Blobs[blob] = [123, 125, 10];
            }
            if (kind == "head") server.Refs[server.Refs.Keys.Single(k => k.Contains("/proposals/", StringComparison.Ordinal))] = server.BaseOid;
            if (kind == "original-bytes") server.Blobs[server.Blobs.Single(pair => pair.Value.AsSpan().SequenceEqual(fixture.Source)).Key] = [7, 8, 9];
            if (kind is "tree" or "mode")
            {
                var oid = server.Commits[server.BaseOid]["tree"]!["sha"]!.GetValue<string>();
                server.Trees[oid] = kind == "tree" ? [] : [server.Trees[oid][0] with { Mode = "120000" }];
            }
            if (kind == "duplicate")
            {
                var copy = server.PullRequests[0].DeepClone().AsObject();
                copy["id"] = 2; copy["number"] = 2; copy["node_id"] = "PR_2";
                var oldHead = copy["head"]!["ref"]!.GetValue<string>();
                copy["head"]!["ref"] = oldHead + "-other";
                server.Refs["refs/heads/" + oldHead + "-other"] = server.Refs["refs/heads/" + oldHead];
                server.PullRequests.Add(copy);
            }
            if (kind is "permission" or "rate-limit")
            {
                server.RejectPath = "/repos/Owner/repo";
                server.RejectionStatus = kind == "permission" ? 403 : 429;
            }
        }
        var writes = server.Mutations;
        AssertResult(await fixture.Run("resume"), exit, outcome);
        Assert.Equal(writes, server.Mutations);
        Assert.Equal(1, server.PrAttempts);
        await fixture.AssertSourceUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_transport_stop_and_timeout_have_closed_process_results(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        fixture.GitHub.DelayResponses = true;
        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_TIMEOUT"] = cancel ? "30000" : "100";
        using var running = CampaignCliProcessTests.Start(fixture.Args("start"), environment);
        try
        {
            if (cancel)
            {
                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    lock (fixture.GitHub.Gate) if (fixture.GitHub.Requests.Count > 0) break;
                    Assert.False(running.Process.HasExited);
                    Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(2));
                    await Task.Delay(20);
                }
                using var signal = Process.Start(new ProcessStartInfo("kill")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "-INT", running.Process.Id.ToString(CultureInfo.InvariantCulture) },
                })!;
                await signal.WaitForExitAsync();
                Assert.Equal(0, signal.ExitCode);
            }
            await running.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            AssertResult(await running.CompleteAsync(), cancel ? 6 : 7, cancel ? "cancelled" : "timeout");
            Assert.Equal(1, fixture.TokenReads());
            Assert.Equal(0, fixture.GitHub.Mutations);
        }
        finally { await CampaignCliProcessTests.StopAsync(running); }
    }

    [Fact]
    public async Task Startup_selected_campaign_terminal_table_crosses_the_real_Program_stream_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rows = new Dictionary<string, (int Exit, string Outcome)>(StringComparer.Ordinal)
        {
            ["complete"] = (0, "no-op"),
            ["no-work"] = (0, "no-op"),
            ["provider-retryable"] = (3, "conflict"),
            ["budget-exhausted"] = (3, "conflict"),
            ["attempt-ambiguous"] = (3, "conflict"),
            ["invalid-configuration"] = (4, "local-invalid"),
            ["state-missing"] = (4, "local-invalid"),
            ["state-present"] = (4, "local-invalid"),
            ["state-corrupt"] = (4, "local-invalid"),
            ["state-unsafe"] = (4, "local-invalid"),
            ["state-conflict"] = (4, "local-invalid"),
            ["lease-conflict"] = (4, "local-invalid"),
            ["lease-unverifiable"] = (4, "local-invalid"),
            ["unsupported-revision"] = (4, "local-invalid"),
            ["incompatible-snapshot"] = (4, "stale"),
            ["patch-stale"] = (4, "stale"),
            ["load-failure"] = (5, "host-failure"),
            ["target-terminal"] = (5, "host-failure"),
            ["provider-terminal"] = (5, "host-failure"),
            ["proposal-invalid"] = (5, "host-failure"),
            ["patch-rejected"] = (5, "host-failure"),
            ["patch-host-failure"] = (5, "host-failure"),
            ["state-publication-failure"] = (5, "host-failure"),
            ["host-contract-error"] = (5, "host-failure"),
            ["cancelled"] = (6, "cancelled"),
            ["timeout"] = (7, "timeout"),
            ["invalid-command"] = (5, "host-failure"),
            ["private-unknown-sentinel"] = (5, "host-failure"),
        };
        foreach (var (source, expected) in rows)
        {
            var environment = fixture.Environment();
            environment["CONTRACTSCRIBE_TEST_GITHUB_TERMINAL"] = "campaign." + source;
            using var process = CampaignCliProcessTests.Start(fixture.Args("start"), environment);
            try
            {
                await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
                var result = await process.CompleteAsync();
                AssertResult(result, expected.Exit, expected.Outcome);
                var contractError = source is "invalid-command" or "private-unknown-sentinel";
                var code = contractError ? "github-proposal.campaign-contract-error" : "campaign." + source;
                Assert.Equal(expected.Exit == 0 ? "" : "github proposal stopped before publication: " + code + "\n", result.Stderr);
                using var json = JsonDocument.Parse(result.Stdout);
                Assert.Equal(12, json.RootElement.GetProperty("checkpointRevision").GetInt64());
                Assert.Equal(contractError ? "presentation" : "campaign", json.RootElement.GetProperty("terminalLayer").GetString());
                foreach (var name in new[] { "publicationOperationId", "generationId", "pullRequestUrl" })
                    Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(name).ValueKind);
            }
            finally { await CampaignCliProcessTests.StopAsync(process); }
        }
        Assert.Equal(0, fixture.TokenReads());
        Assert.Empty(fixture.GitHub.Requests);
    }
}
