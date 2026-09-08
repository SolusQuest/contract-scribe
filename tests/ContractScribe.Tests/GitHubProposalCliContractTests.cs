using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed class GitHubProposalCliContractTests
{
    private static readonly CliBuildIdentity Identity = new("test+" + new string('a', 40), new('a', 40), new('a', 40));

    public static TheoryData<string, string, int> CampaignRows => new()
    {
        { "complete", "no-op", 0 }, { "no-work", "no-op", 0 },
        { "provider-retryable", "conflict", 3 }, { "budget-exhausted", "conflict", 3 }, { "attempt-ambiguous", "conflict", 3 },
        { "invalid-configuration", "local-invalid", 4 }, { "state-missing", "local-invalid", 4 }, { "state-present", "local-invalid", 4 },
        { "state-corrupt", "local-invalid", 4 }, { "state-unsafe", "local-invalid", 4 }, { "state-conflict", "local-invalid", 4 },
        { "lease-conflict", "local-invalid", 4 }, { "lease-unverifiable", "local-invalid", 4 }, { "unsupported-revision", "local-invalid", 4 },
        { "incompatible-snapshot", "stale", 4 }, { "patch-stale", "stale", 4 },
        { "load-failure", "host-failure", 5 }, { "target-terminal", "host-failure", 5 }, { "provider-terminal", "host-failure", 5 },
        { "proposal-invalid", "host-failure", 5 }, { "patch-rejected", "host-failure", 5 }, { "patch-host-failure", "host-failure", 5 },
        { "state-publication-failure", "host-failure", 5 }, { "host-contract-error", "host-failure", 5 },
        { "cancelled", "cancelled", 6 }, { "timeout", "timeout", 7 },
        { "invalid-command", "host-failure", 5 }, { "unknown-private-sentinel", "host-failure", 5 },
    };

    [Theory]
    [MemberData(nameof(CampaignRows))]
    public void Every_campaign_source_has_frozen_bytes_nulls_diagnostic_and_exit(string source, string outcome, int exit)
    {
        var actual = GitHubProposalPresentation.Campaign(Identity, new("execution", CampaignOperation.Resume, "campaign." + source, 12));
        Assert.Equal(exit, actual.ExitCode);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        GitHubProposalPresentation.Write(actual, stdout, stderr);
        var contractError = source is "invalid-command" or "unknown-private-sentinel";
        var code = contractError ? "github-proposal.campaign-contract-error" : "campaign." + source;
        var expected = "{\"githubProposalEnvelopeVersion\":1,\"terminalLayer\":\"" + (contractError ? "presentation" : "campaign")
            + "\",\"cliContractBaseline\":\"" + new string('a', 40) + "\",\"toolVersion\":\"test+" + new string('a', 40)
            + "\",\"campaignOperation\":\"resume\",\"publicationOperationId\":null,\"generationId\":null,\"outcome\":\"github-proposal."
            + outcome + "\",\"diagnosticCodes\":[" + (exit == 0 ? "" : "\"" + code + "\"")
            + "],\"checkpointRevision\":12,\"pullRequestUrl\":null}\n";
        Assert.Equal(expected, stdout.ToString());
        Assert.Equal(exit == 0 ? "" : "github proposal stopped before publication: " + code + "\n", stderr.ToString());
    }

    [Theory]
    [InlineData("unknown-command")]
    [InlineData("unknown-option")]
    [InlineData("missing-required-option")]
    [InlineData("duplicate-option")]
    [InlineData("missing-option-value")]
    [InlineData("invalid-option-value")]
    [InlineData("unexpected-operand")]
    [InlineData("forbidden-combination")]
    public void Usage_has_existing_diagnostics_and_explicit_unavailable_identity(string usage)
    {
        var code = "cli.usage." + usage;
        var actual = GitHubProposalPresentation.Usage(Identity, new(usage, code, null));
        Assert.Equal(2, actual.ExitCode);
        using var json = JsonDocument.Parse(actual.StandardOutput);
        foreach (var property in new[] { "campaignOperation", "publicationOperationId", "generationId", "checkpointRevision", "pullRequestUrl" })
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(property).ValueKind);
        Assert.Equal(CliDiagnostics.Create(code).ToLine(), Assert.Single(actual.Diagnostics).ToLine());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Seven_options_accept_both_forms_and_any_order(bool equals)
    {
        var values = new[] { "--repository-root", "--input", "--policy", "--snapshot", "--state", "--configuration", "--github-configuration" };
        var args = new List<string> { "resume" };
        foreach (var name in values.Reverse())
            if (equals) args.Add(name + "=value");
            else args.AddRange([name, "value"]);
        var parsed = GitHubProposalCommandParser.Parse(args.ToArray());
        Assert.Null(parsed.Failure);
        Assert.NotNull(parsed.Arguments);
        foreach (var name in values)
        {
            var duplicate = args.Concat([name, "value"]).ToArray();
            Assert.Equal("duplicate-option", GitHubProposalCommandParser.Parse(duplicate).Failure!.UsageClass);
        }
    }

    [Theory]
    [InlineData("Start", "unknown-command")]
    [InlineData("@response", "unknown-command")]
    [InlineData("start --wat x", "unknown-option")]
    [InlineData("start -r x", "unknown-option")]
    [InlineData("start operand", "unexpected-operand")]
    [InlineData("start --state=", "missing-option-value")]
    [InlineData("start --help --state=x", "forbidden-combination")]
    public void Parser_is_ordinal_and_closed(string text, string usage) =>
        Assert.Equal(usage, GitHubProposalCommandParser.Parse(text.Split(' ')).Failure!.UsageClass);

    [Fact]
    public void Facade_uses_observed_predecessor_identity_and_authenticated_repository_url()
    {
        var authority = GitHubProposalBranchTests.Authority(new());
        var predecessor = GitHubPublicationResult.AwaitingReview(new(7, "old-generation", "refs/heads/old", new('a', 40), new('b', 64)));
        var actual = GitHubPublicationFacade.Observe(authority, predecessor, new(1, "node", "Owner", "repo"));
        Assert.Null(actual.OperationId);
        Assert.Equal("old-generation", actual.GenerationId);
        Assert.Equal("https://github.com/Owner/repo/pull/7", actual.PullRequestUrl);
        var failure = GitHubPublicationFacade.Observe(authority,
            GitHubPublicationResult.FromRemoteFailure(GitHubPublicationRemoteFailureKind.Permission), new(1, "node", "Owner", "repo"));
        Assert.Null(failure.OperationId); Assert.Null(failure.GenerationId); Assert.Null(failure.PullRequestUrl);
    }

    [Theory]
    [InlineData("admitted")]
    [InlineData("recovered-content-partial")]
    [InlineData("recovered-ref-partial")]
    public void Incomplete_publication_states_keep_distinct_diagnosed_conflicts(string kind)
    {
        var result = kind switch
        {
            "admitted" => GitHubPublicationResult.Admitted(new("refs/heads/claim", new('a', 40), "op", new('b', 64))),
            "recovered-content-partial" => GitHubPublicationResult.RecoveredContentPartial(new(GitHubPublicationResourceKind.Blob, new('a', 40), new('b', 64))),
            _ => GitHubPublicationResult.RecoveredRefPartial(new("refs/heads/proposal", new('a', 40), new('b', 40), new('c', 64))),
        };
        var actual = GitHubProposalPresentation.Publication(Identity, CampaignOperation.Start, 14, new(result, "op", "gen", null));
        Assert.Equal(3, actual.ExitCode);
        Assert.Equal("github-proposal." + kind, Assert.Single(actual.Diagnostics).Code);
        Assert.Contains("\"outcome\":\"github-proposal.conflict\"", actual.StandardOutput);
    }
}
