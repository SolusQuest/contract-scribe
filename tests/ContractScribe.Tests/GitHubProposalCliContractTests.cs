using System.Text.Json;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed class GitHubProposalCliContractTests
{
    private static readonly CliBuildIdentity Identity = new("test+" + new string('a', 40), new('a', 40), new('a', 40));

    [Fact]
    public void Publication_diagnostic_has_fixed_order_nulls_and_no_effect_on_terminal()
    {
        var diagnostic = new GitHubPublicationDiagnostic(GitHubPublicationBoundary.GitCreateContent,
            GitHubPublicationOwner.GitData, ProposalFailure: GitHubProposalFailureKind.Unresolved,
            Delivery: GitHubDelivery.NeedsReadback, RecoveryCode: GitHubFailureCode.NotFound,
            RecoveryHttpStatus: 404, ObjectKind: GitHubObjectKind.Tree);
        var result = Present(diagnostic);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("{\"boundary\":\"GitCreateContent\",\"owner\":\"GitData\",\"coordinationFailure\":null,"
            + "\"proposalFailure\":\"Unresolved\",\"pullRequestOutcome\":null,\"transportCode\":null,"
            + "\"transportHttpStatus\":null,\"delivery\":\"NeedsReadback\",\"recoveryCode\":\"NotFound\","
            + "\"recoveryHttpStatus\":404,\"objectKind\":\"Tree\",\"predicate\":null}",
            json.RootElement.GetProperty("publicationDiagnostic").GetRawText());
        Assert.Equal("publicationDiagnostic", json.RootElement.EnumerateObject().Last().Name);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("github-proposal.conflict", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("github proposal publication stopped: github-proposal.conflict", Assert.Single(result.Diagnostics).Message);
        var authority = GitHubProposalBranchTests.Authority(new());
        var observed = GitHubPublicationFacade.Observe(authority,
            GitHubPublicationResult.FromRemoteFailure(GitHubPublicationRemoteFailureKind.Conflict), null, diagnostic);
        Assert.Same(diagnostic, observed.Diagnostic);
        Assert.DoesNotContain("NotFound", diagnostic.ToString());
    }

    [Fact]
    public void Defined_diagnostic_values_are_closed_and_undefined_values_do_not_replace_the_terminal()
    {
        var basis = new GitHubPublicationDiagnostic(GitHubPublicationBoundary.Reconcile, GitHubPublicationOwner.Reconciler);
        void Check<T>(string field, Func<T, GitHubPublicationDiagnostic> set) where T : struct, Enum
        {
            foreach (var value in Enum.GetValues<T>())
            {
                using var json = JsonDocument.Parse(Present(set(value)).StandardOutput);
                Assert.Equal(value.ToString(), json.RootElement.GetProperty("publicationDiagnostic").GetProperty(field).GetString());
                Assert.True(Present(set(value)).StandardOutput.Length < 2048);
            }
            var invalid = Present(set((T)Enum.ToObject(typeof(T), -1)));
            using var rejected = JsonDocument.Parse(invalid.StandardOutput);
            Assert.Equal(JsonValueKind.Null, rejected.RootElement.GetProperty("publicationDiagnostic").ValueKind);
            Assert.Equal(3, invalid.ExitCode);
            Assert.Equal("github-proposal.conflict", Assert.Single(invalid.Diagnostics).Code);
        }
        Check<GitHubPublicationBoundary>("boundary", v => basis with { Boundary = v });
        Check<GitHubPublicationOwner>("owner", v => basis with { Owner = v });
        Check<GitHubCoordinationFailureKind>("coordinationFailure", v => basis with { CoordinationFailure = v });
        Check<GitHubProposalFailureKind>("proposalFailure", v => basis with { ProposalFailure = v });
        Check<ContractScribe.GitHub.PullRequests.GitHubProposalOutcome>("pullRequestOutcome", v => basis with { PullRequestOutcome = v });
        Check<GitHubFailureCode>("transportCode", v => basis with { TransportCode = v });
        Check<GitHubFailureCode>("recoveryCode", v => basis with { RecoveryCode = v });
        Check<GitHubDelivery>("delivery", v => basis with { Delivery = v });
        Check<GitHubObjectKind>("objectKind", v => basis with { ObjectKind = v });
        Check<GitHubPublicationPredicate>("predicate", v => basis with { Predicate = v });
        foreach (var status in new[] { int.MinValue, 99, 100, 599, 600, int.MaxValue })
        {
            foreach (var recovery in new[] { false, true })
            {
                var diagnostic = recovery ? basis with { RecoveryHttpStatus = status } : basis with { TransportHttpStatus = status };
                var rendered = Present(diagnostic);
                using var json = JsonDocument.Parse(rendered.StandardOutput);
                Assert.Equal(status is >= 100 and <= 599 ? JsonValueKind.Object : JsonValueKind.Null,
                    json.RootElement.GetProperty("publicationDiagnostic").ValueKind);
                Assert.Equal(3, rendered.ExitCode);
            }
        }
    }

    [Fact]
    public void Success_suppresses_an_injected_failure_diagnostic()
    {
        var diagnostic = new GitHubPublicationDiagnostic(GitHubPublicationBoundary.Reconcile,
            GitHubPublicationOwner.Reconciler, Predicate: GitHubPublicationPredicate.UnhandledException);
        var result = GitHubProposalPresentation.Publication(Identity, CampaignOperation.Resume, 15,
            new(GitHubPublicationResult.ReplayNoOp(new("refs/heads/claim", new('a', 40), "op", new('b', 64))),
                "op", "gen", null, diagnostic));
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publicationDiagnostic").ValueKind);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    private static CliExecutionResult Present(GitHubPublicationDiagnostic diagnostic) =>
        GitHubProposalPresentation.Publication(Identity, CampaignOperation.Start, 14,
            new(GitHubPublicationResult.FromRemoteFailure(GitHubPublicationRemoteFailureKind.Conflict), null, null, null, diagnostic));

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
            + "],\"checkpointRevision\":12,\"pullRequestUrl\":null,\"publicationDiagnostic\":null}\n";
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
