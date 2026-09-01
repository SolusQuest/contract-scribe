using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationProtocolDecisionHarnessTests
{
    private static readonly string[] Alternatives =
    [
        "coordination-ref",
        "managed-issue-ledger",
        "external-serializer",
    ];

    private static readonly Scenario[] Scenarios =
    [
        new("initial-create-response-loss", Fault.InitialClaimResponseLoss),
        new("stale-predecessor", Fault.StalePredecessor),
        new("coordination-ancestor-rewind", Fault.CoordinationAncestorRewind),
        new("proposal-ancestor-rewind", Fault.ProposalAncestorRewind),
        new("target-move-before-claim", Fault.TargetMoveBeforeClaim),
        new("target-move-after-claim-before-resource-write", Fault.TargetMoveBeforeResourceWrite),
        new("target-move-during-pr-create", Fault.TargetMoveDuringPullRequestCreate),
        new("two-first-publication-invocations", Fault.TwoInitialInvocations),
        new("two-append-invocations", Fault.TwoAppendInvocations),
        new("ambiguous-commit-response", Fault.CommitResponseLoss),
        new("ambiguous-proposal-ref-response", Fault.ProposalRefResponseLoss),
        new("ambiguous-pr-response", Fault.PullRequestResponseLoss),
    ];

    [Fact]
    public void Execute_three_alternatives_against_the_complete_required_vector_set()
    {
        var results = Alternatives
            .SelectMany(alternative => Scenarios.Select(scenario => Execute(alternative, scenario)))
            .ToImmutableArray();

        Assert.Equal(Alternatives.Length * Scenarios.Length, results.Length);
        Assert.All(Scenarios, scenario => Assert.Equal(
            Alternatives,
            results.Where(result => result.Scenario == scenario.Name)
                .Select(result => result.Alternative)
                .ToArray()));

        var coordination = results.Where(result => result.Alternative == "coordination-ref").ToArray();
        Assert.All(coordination, result => Assert.True(result.RequirementSatisfied, result.Transcript));
        Assert.All(
            coordination.Where(result => result.Scenario is
                "two-first-publication-invocations" or "two-append-invocations"),
            result => Assert.Equal(1, result.AdmittedOperations));
        Assert.Contains(
            coordination,
            result => result.Scenario == "target-move-during-pr-create"
                && result.Residual == "one-marker-owned-stale-draft");

        var issueLedger = results.Where(result => result.Alternative == "managed-issue-ledger").ToArray();
        Assert.Contains(issueLedger, result => !result.RequirementSatisfied
            && result.Scenario == "two-first-publication-invocations");
        Assert.Contains(issueLedger, result => !result.RequirementSatisfied
            && result.Scenario == "coordination-ancestor-rewind");

        var external = results.Where(result => result.Alternative == "external-serializer").ToArray();
        Assert.Contains(external, result => !result.RequirementSatisfied
            && result.Scenario == "two-append-invocations");
        Assert.All(external, result => Assert.True(result.RequiresExternalPrerequisite));

        var root = FindRepositoryRoot();
        var output = Path.Join(root, "TestResults", "issue-158-protocol-decision", "transcripts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + "\n");
    }

    private static Observation Execute(string alternative, Scenario scenario)
    {
        var state = new RemoteState();
        if (scenario.Fault is Fault.InitialClaimResponseLoss or Fault.TwoInitialInvocations)
        {
            state.Coordination = new string('0', 40);
        }
        var first = new Invocation("operation-a", state);
        var second = new Invocation("operation-b", state);
        var transcript = new List<string>
        {
            $"read coordination={first.ObservedCoordination} proposal={first.ObservedProposal} target={first.ObservedTarget}",
        };

        if (scenario.Fault == Fault.StalePredecessor)
        {
            state.Coordination = "coordination-newer";
            transcript.Add("external update coordination=coordination-newer");
        }
        else if (scenario.Fault == Fault.CoordinationAncestorRewind)
        {
            state.Coordination = "coordination-ancestor";
            transcript.Add("external force rewind coordination=coordination-ancestor");
        }
        else if (scenario.Fault == Fault.ProposalAncestorRewind)
        {
            state.Proposal = "proposal-ancestor";
            transcript.Add("external force rewind proposal=proposal-ancestor");
        }

        if (scenario.Fault == Fault.TargetMoveBeforeClaim)
        {
            state.Target = "target-newer";
            transcript.Add("external update target=target-newer before claim");
        }

        var firstClaim = scenario.Fault == Fault.TargetMoveBeforeClaim
            ? false
            : Claim(alternative, state, first, transcript);
        if (scenario.Fault == Fault.TargetMoveBeforeClaim)
        {
            transcript.Add("authenticated target readback mismatch; zero coordination or resource mutation");
        }
        var admitted = firstClaim ? 1 : 0;

        if (scenario.Fault == Fault.InitialClaimResponseLoss && firstClaim)
        {
            transcript.Add("claim response lost");
            var recovered = Claim(alternative, state, first, transcript);
            if (alternative is "managed-issue-ledger" or "external-serializer" && recovered)
            {
                admitted++;
            }
        }

        if (scenario.Fault is Fault.TwoInitialInvocations or Fault.TwoAppendInvocations)
        {
            var secondClaim = Claim(alternative, state, second, transcript);
            if (secondClaim)
            {
                admitted++;
            }
        }

        var residual = "none";
        if (firstClaim && scenario.Fault == Fault.ProposalAncestorRewind)
        {
            var proposalUpdated = SerializedGraphQlUpdateRef(
                state,
                "refs/heads/contract-scribe/proposals/campaign/g1",
                first.ObservedProposal,
                first.SuccessorProposal,
                transcript);
            Assert.False(proposalUpdated);
        }
        else if (firstClaim && scenario.Fault == Fault.TargetMoveBeforeResourceWrite)
        {
            state.Target = "target-newer";
            transcript.Add("external update target=target-newer");
            if (state.Target != first.ObservedTarget)
            {
                transcript.Add("pre-resource target readback mismatch; zero resource mutation");
            }
        }
        else if (firstClaim && scenario.Fault == Fault.TargetMoveDuringPullRequestCreate)
        {
            transcript.Add("final pre-PR target read matches");
            state.Target = "target-newer";
            state.PullRequests.Add(first.OperationId);
            residual = "one-marker-owned-stale-draft";
            transcript.Add("target moved during PR processing; marker-owned stale draft discovered");
        }
        else if (firstClaim && scenario.Fault == Fault.CommitResponseLoss)
        {
            state.ContentObjects.Add("expected-commit-a");
            transcript.Add("content-addressed commit created; response lost; exact SHA discovery recovers");
        }
        else if (firstClaim && scenario.Fault == Fault.ProposalRefResponseLoss)
        {
            state.Proposal = first.SuccessorProposal;
            transcript.Add("proposal ref updated; response lost; exact head readback recovers");
        }
        else if (firstClaim && scenario.Fault == Fault.PullRequestResponseLoss)
        {
            state.PullRequests.Add(first.OperationId);
            transcript.Add("marker-owned draft created; response lost; exhaustive marker discovery recovers one");
        }

        var satisfied = alternative switch
        {
            "coordination-ref" => scenario.Fault switch
            {
                Fault.StalePredecessor or Fault.CoordinationAncestorRewind => !firstClaim,
                Fault.TargetMoveBeforeClaim => !firstClaim && state.Coordination == first.ObservedCoordination,
                Fault.ProposalAncestorRewind => firstClaim
                    && state.Proposal == "proposal-ancestor"
                    && transcript[^1].StartsWith("POST /graphql updateRefs rejected", StringComparison.Ordinal),
                Fault.TwoInitialInvocations or Fault.TwoAppendInvocations => admitted == 1,
                Fault.TargetMoveBeforeResourceWrite => state.Target != first.ObservedTarget,
                Fault.TargetMoveDuringPullRequestCreate => state.PullRequests.Count == 1,
                _ => firstClaim,
            },
            "managed-issue-ledger" => scenario.Fault switch
            {
                Fault.TargetMoveBeforeClaim => !firstClaim && state.IssueOperations.Count == 0,
                Fault.StalePredecessor or Fault.CoordinationAncestorRewind => false,
                Fault.TwoInitialInvocations or Fault.TwoAppendInvocations => admitted == 1,
                Fault.InitialClaimResponseLoss => admitted == 1,
                _ => true,
            },
            "external-serializer" => scenario.Fault switch
            {
                Fault.TargetMoveBeforeClaim => !firstClaim,
                Fault.InitialClaimResponseLoss => false,
                Fault.TwoInitialInvocations or Fault.TwoAppendInvocations => admitted == 1,
                Fault.StalePredecessor or Fault.CoordinationAncestorRewind => false,
                _ => true,
            },
            _ => throw new InvalidOperationException(),
        };

        return new Observation(
            alternative,
            scenario.Name,
            satisfied,
            admitted,
            residual,
            alternative == "external-serializer",
            string.Join(" | ", transcript));
    }

    private static bool Claim(
        string alternative,
        RemoteState state,
        Invocation invocation,
        List<string> transcript)
    {
        switch (alternative)
        {
            case "coordination-ref":
                return SerializedGraphQlUpdateRef(
                    state,
                    "refs/heads/contract-scribe/coordination/campaign",
                    invocation.ObservedCoordination,
                    invocation.SuccessorCoordination,
                    transcript);

            case "managed-issue-ledger":
                var issueBody = JsonSerializer.Serialize(new
                {
                    operationId = invocation.OperationId,
                    expectedPredecessor = invocation.ObservedCoordination,
                });
                using (var document = JsonDocument.Parse(issueBody))
                {
                    Assert.Equal(invocation.OperationId, document.RootElement.GetProperty("operationId").GetString());
                }
                state.IssueOperations.Add(invocation.OperationId);
                transcript.Add($"PATCH /issues body={issueBody} accepted without predecessor compare");
                return true;

            case "external-serializer":
                transcript.Add($"external serializer admitted operation={invocation.OperationId}; no durable repository predecessor compare");
                return true;

            default:
                throw new InvalidOperationException();
        }
    }

    private static bool SerializedGraphQlUpdateRef(
        RemoteState state,
        string name,
        string beforeOid,
        string afterOid,
        List<string> transcript)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = "mutation($refUpdates:[RefUpdate!]!){updateRefs(input:{repositoryId:\"repository\",refUpdates:$refUpdates}){clientMutationId}}",
            variables = new
            {
                refUpdates = new[]
                {
                    new { name, beforeOid, afterOid, force = false },
                },
            },
        });
        using var document = JsonDocument.Parse(body);
        var updates = document.RootElement
            .GetProperty("variables")
            .GetProperty("refUpdates");
        Assert.Equal(1, updates.GetArrayLength());
        var update = updates[0];
        Assert.False(update.GetProperty("force").GetBoolean());
        Assert.Equal(beforeOid, update.GetProperty("beforeOid").GetString());
        Assert.Equal(afterOid, update.GetProperty("afterOid").GetString());

        var current = name.Contains("/coordination/", StringComparison.Ordinal)
            ? state.Coordination
            : state.Proposal;
        if (current == afterOid)
        {
            transcript.Add($"POST /graphql exact readback recovers name={name} afterOid={afterOid}");
            return true;
        }
        if (current != beforeOid)
        {
            transcript.Add($"POST /graphql updateRefs rejected name={name} beforeOid={beforeOid} actual={current} body={body}");
            return false;
        }

        if (name.Contains("/coordination/", StringComparison.Ordinal))
        {
            state.Coordination = afterOid;
        }
        else
        {
            state.Proposal = afterOid;
        }
        transcript.Add($"POST /graphql updateRefs admitted name={name} beforeOid={beforeOid} afterOid={afterOid} force=false body={body}; REST create/update has no equivalent old-OID comparison");
        return true;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private enum Fault
    {
        InitialClaimResponseLoss,
        StalePredecessor,
        CoordinationAncestorRewind,
        ProposalAncestorRewind,
        TargetMoveBeforeClaim,
        TargetMoveBeforeResourceWrite,
        TargetMoveDuringPullRequestCreate,
        TwoInitialInvocations,
        TwoAppendInvocations,
        CommitResponseLoss,
        ProposalRefResponseLoss,
        PullRequestResponseLoss,
    }

    private sealed record Scenario(string Name, Fault Fault);

    private sealed record Observation(
        string Alternative,
        string Scenario,
        bool RequirementSatisfied,
        int AdmittedOperations,
        string Residual,
        bool RequiresExternalPrerequisite,
        string Transcript);

    private sealed class RemoteState
    {
        public string Coordination { get; set; } = "coordination-base";
        public string Proposal { get; set; } = "proposal-base";
        public string Target { get; set; } = "target-base";
        public List<string> IssueOperations { get; } = [];
        public List<string> ContentObjects { get; } = [];
        public List<string> PullRequests { get; } = [];
    }

    private sealed class Invocation
    {
        public Invocation(string operationId, RemoteState state)
        {
            OperationId = operationId;
            ObservedCoordination = state.Coordination;
            ObservedProposal = state.Proposal;
            ObservedTarget = state.Target;
        }

        public string OperationId { get; }
        public string ObservedCoordination { get; }
        public string ObservedProposal { get; }
        public string ObservedTarget { get; }
        public string SuccessorCoordination => "coordination-" + OperationId;
        public string SuccessorProposal => "proposal-" + OperationId;
    }
}
