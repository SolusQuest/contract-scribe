using System.Text.Json;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class GitHubProposalHttpLoss0BeforeTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(false, 0);
}

public sealed class GitHubProposalHttpLoss0AfterTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(true, 0);
}

public sealed class GitHubProposalHttpLoss1BeforeTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(false, 1);
}

public sealed class GitHubProposalHttpLoss1AfterTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(true, 1);
}

public sealed class GitHubProposalHttpLoss2BeforeTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(false, 2);
}

public sealed class GitHubProposalHttpLoss2AfterTests
{
    [Fact]
    public Task Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes() =>
        GitHubProposalHttpLossScenario.RunAsync(true, 2);
}

// Separate classes use the existing xUnit workers in a full run; CI assigns each
// class its own runner. Every case owns separate files, services and child state.
internal static class GitHubProposalHttpLossScenario
{
    internal static async Task RunAsync(bool after, int lane)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await GitHubProposalCliProcessTests.Fixture.CreateAsync();
        GitHubProposalCliProcessTests.AssertResult(await fixture.Run("start"), 0, "published");
        var mutations = fixture.GitHub.Mutations;
        Assert.InRange(mutations, 20, 500);
        Assert.Equal(new[] { "blobs", "cas", "commits", "pulls", "trees" },
            fixture.GitHub.Requests.Where(request => request.Mutation > 0)
                .Select(request => request.Kind).Distinct().OrderBy(kind => kind, StringComparer.Ordinal));
        for (var index = lane + 1; index <= mutations; index += 3)
        {
            fixture.GitHub.Reset();
            fixture.GitHub.LoseAt = index;
            fixture.GitHub.LoseAfter = after;
            var failed = await fixture.Run("start");
            if (failed.ExitCode == 4)
            {
                GitHubProposalCliProcessTests.AssertResult(failed, 4, "local-invalid");
                using var selected = JsonDocument.Parse(failed.Stdout);
                Assert.Equal(new[] { "campaign.state-conflict" }, selected.RootElement
                    .GetProperty("diagnosticCodes").EnumerateArray().Select(code => code.GetString()));
            }
            else Assert.True(failed.ExitCode is 0 or 3 or 5,
                $"mutation={index}, after={after}, stdout={failed.Stdout}, stderr={failed.Stderr}");
            var boundary = Assert.Single(fixture.GitHub.Requests, r => r.Mutation == index);
            Assert.Contains(boundary.Kind, new[] { "blobs", "cas", "commits", "pulls", "trees" });
            fixture.GitHub.LoseAt = null;
            // A lost invocation does not release another process's lease. Advance only
            // the fake server's clock; no production timeout or ownership check is bypassed.
            fixture.GitHub.ServerNow = fixture.GitHub.ServerNow.AddMinutes(6);
            var hasCheckpoint = fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal))
                && fixture.GitHub.CoordinationDocument()["checkpoint"] is not null;
            var restarted = await fixture.Run(hasCheckpoint ? "resume" : "start");
            Assert.True(restarted.ExitCode is 0 or 3,
                $"recovery mutation={index}, after={after}, stdout={restarted.Stdout}, stderr={restarted.Stderr}");
            var writes = fixture.GitHub.PublicationWrites;
            var stable = await fixture.Run("resume");
            Assert.Equal(writes, fixture.GitHub.PublicationWrites);
            Assert.True(stable.ExitCode is 0 or 3,
                $"replay mutation={index}, after={after}, stdout={stable.Stdout}, stderr={stable.Stderr}");
            Assert.InRange(fixture.GitHub.PrAttempts, 0, 1);
            Assert.InRange(fixture.GitHub.PullRequests.Count, 0, 1);
            Assert.False(File.Exists(fixture.State));
            Assert.All(fixture.GitHub.Requests, r => Assert.True(r.Method == "GET" || r.Method == "POST"));
            if (stable.ExitCode == 0)
            {
                GitHubProposalCliProcessTests.AssertResult(stable, 0, "awaiting-review");
                Assert.Single(fixture.GitHub.PullRequests);
                Assert.Equal("published", fixture.GitHub.Coordination()["stage"]!.GetValue<string>());
            }
            await fixture.AssertSourceUnchanged();
        }
    }
}
