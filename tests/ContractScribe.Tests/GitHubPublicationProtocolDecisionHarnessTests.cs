using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationProtocolDecisionHarnessTests
{
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
    public void Execute_three_viable_alternatives_through_one_serialized_fault_boundary()
    {
        IAlternative[] alternatives =
        [
            new CoordinationRefAlternative(),
            new ManagedIssueLedgerAlternative(),
            new ExternalSerializerAlternative(),
        ];
        var results = alternatives.SelectMany(alternative =>
                Scenarios.Select(scenario => Execute(alternative, scenario)))
            .ToImmutableArray();

        Assert.Equal(alternatives.Length * Scenarios.Length, results.Length);
        Assert.All(Scenarios, scenario => Assert.Equal(
            alternatives.Select(alternative => alternative.Id),
            results.Where(result => result.Scenario == scenario.Name)
                .Select(result => result.Alternative)));
        Assert.All(results.Where(result => result.Alternative == "coordination-ref"),
            result => Assert.True(result.RequirementSatisfied, result.Transcript));
        Assert.All(results.Where(result => result.Alternative == "external-serializer"),
            result => Assert.True(result.RequiresExternalPrerequisite));
        Assert.Contains(results, result => result.Alternative == "external-serializer"
            && result.Scenario == "two-first-publication-invocations"
            && result.RequirementSatisfied);
        Assert.Contains(results, result => result.Alternative == "managed-issue-ledger"
            && result.Scenario == "two-first-publication-invocations"
            && !result.RequirementSatisfied
            && !result.AdmissionFinality);

        var root = FindRepositoryRoot();
        var output = Path.Join(root, "TestResults", "issue-158-protocol-decision-v2",
            "transcripts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static Observation Execute(IAlternative alternative, Scenario scenario)
    {
        var server = new SerializedFaultServer();
        if (scenario.Fault is Fault.InitialClaimResponseLoss or Fault.TwoInitialInvocations)
        {
            server.CoordinationOid = Zero;
        }
        var first = new Invocation("operation-a", server);
        var second = new Invocation("operation-b", server);

        if (scenario.Fault == Fault.StalePredecessor)
        {
            server.CoordinationOid = Oid('8');
            server.RecordExternal("coordination", Oid('8'));
        }
        else if (scenario.Fault == Fault.CoordinationAncestorRewind)
        {
            server.CoordinationOid = Oid('7');
            server.RecordExternal("coordination-rewind", Oid('7'));
        }
        else if (scenario.Fault == Fault.ProposalAncestorRewind)
        {
            server.ProposalOid = Oid('6');
            server.RecordExternal("proposal-rewind", Oid('6'));
        }
        else if (scenario.Fault == Fault.TargetMoveBeforeClaim)
        {
            server.TargetOid = Oid('9');
            server.RecordExternal("target-before-claim", Oid('9'));
        }

        var firstAdmitted = scenario.Fault == Fault.TargetMoveBeforeClaim
            ? false
            : alternative.Admit(server, first,
                loseResponse: scenario.Fault == Fault.InitialClaimResponseLoss);
        if (scenario.Fault == Fault.TargetMoveBeforeClaim)
        {
            server.Record("gate", "target-mismatch", "zero-mutation");
        }
        var admitted = firstAdmitted ? 1 : 0;

        if (scenario.Fault == Fault.InitialClaimResponseLoss && firstAdmitted)
        {
            if (alternative.Admit(server, first, loseResponse: false))
            {
                server.Record("replay", first.OperationId, "exact-readback");
            }
        }
        if (scenario.Fault is Fault.TwoInitialInvocations or Fault.TwoAppendInvocations)
        {
            if (alternative.Admit(server, second, loseResponse: false))
            {
                admitted++;
            }
        }

        var residual = "none";
        if (firstAdmitted && scenario.Fault == Fault.ProposalAncestorRewind)
        {
            var updated = server.Send(new WireRequest(
                "graphql-update-ref", first.OperationId, "proposal",
                first.ObservedProposalOid, first.SuccessorProposalOid,
                LoseResponse: false));
            residual = updated.Outcome == "rejected" ? "proposal-conflict" : "unexpected-write";
        }
        else if (firstAdmitted && scenario.Fault == Fault.TargetMoveBeforeResourceWrite)
        {
            server.TargetOid = Oid('9');
            server.RecordExternal("target-before-resource", Oid('9'));
            if (server.TargetOid != first.ObservedTargetOid)
            {
                server.Record("gate", "target-mismatch", "zero-resource-mutation");
            }
        }
        else if (firstAdmitted && scenario.Fault == Fault.TargetMoveDuringPullRequestCreate)
        {
            server.Record("gate", "final-target-read", "exact");
            server.TargetOid = Oid('9');
            server.Send(new WireRequest(
                "create-pr", first.OperationId, "pull-request", null, null,
                LoseResponse: false));
            residual = "one-marker-owned-stale-draft";
        }
        else if (firstAdmitted && scenario.Fault == Fault.CommitResponseLoss)
        {
            server.Send(new WireRequest(
                "create-content", first.OperationId, "commit", null,
                first.ExpectedCommitOid, LoseResponse: true));
            var recovered = server.Send(new WireRequest(
                "read-content", first.OperationId, "commit", null,
                first.ExpectedCommitOid, LoseResponse: false));
            residual = recovered.Outcome;
        }
        else if (firstAdmitted && scenario.Fault == Fault.ProposalRefResponseLoss)
        {
            server.Send(new WireRequest(
                "graphql-update-ref", first.OperationId, "proposal",
                first.ObservedProposalOid, first.SuccessorProposalOid,
                LoseResponse: true));
            var recovered = server.Send(new WireRequest(
                "read-ref", first.OperationId, "proposal", null,
                first.SuccessorProposalOid, LoseResponse: false));
            residual = recovered.Outcome;
        }
        else if (firstAdmitted && scenario.Fault == Fault.PullRequestResponseLoss)
        {
            server.Send(new WireRequest(
                "create-pr", first.OperationId, "pull-request", null, null,
                LoseResponse: true));
            var recovered = server.Send(new WireRequest(
                "discover-pr", first.OperationId, "pull-request", null, null,
                LoseResponse: false));
            residual = recovered.Outcome;
        }

        var metrics = new Metrics(
            admitted,
            server.ResourceWrites,
            server.PullRequests.Count,
            residual,
            alternative.RepositoryBoundAdmission,
            alternative.AdmissionFinality,
            alternative.RequiresExternalPrerequisite);
        return new Observation(
            alternative.Id,
            scenario.Name,
            Evaluate(scenario.Fault, metrics),
            metrics.AdmittedOperations,
            metrics.ResourceWrites,
            metrics.PullRequestCount,
            metrics.Residual,
            metrics.RepositoryBoundAdmission,
            metrics.AdmissionFinality,
            metrics.RequiresExternalPrerequisite,
            string.Join(" | ", server.Transcript));
    }

    private static bool Evaluate(Fault fault, Metrics metrics) => fault switch
    {
        Fault.TargetMoveBeforeClaim or Fault.StalePredecessor or Fault.CoordinationAncestorRewind =>
            metrics.AdmittedOperations == 0 && metrics.ResourceWrites == 0,
        Fault.ProposalAncestorRewind =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "proposal-conflict",
        Fault.TargetMoveBeforeResourceWrite =>
            metrics.AdmittedOperations == 1 && metrics.ResourceWrites == 0,
        Fault.TargetMoveDuringPullRequestCreate =>
            metrics.PullRequestCount == 1 && metrics.Residual == "one-marker-owned-stale-draft",
        Fault.TwoInitialInvocations or Fault.TwoAppendInvocations =>
            metrics.AdmittedOperations == 1 && metrics.AdmissionFinality,
        Fault.CommitResponseLoss or Fault.ProposalRefResponseLoss or Fault.PullRequestResponseLoss =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "exact-recovered",
        Fault.InitialClaimResponseLoss => metrics.AdmittedOperations == 1,
        _ => false,
    };

    private interface IAlternative
    {
        string Id { get; }
        bool RepositoryBoundAdmission { get; }
        bool AdmissionFinality { get; }
        bool RequiresExternalPrerequisite { get; }
        bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse);
    }

    private sealed class CoordinationRefAlternative : IAlternative
    {
        public string Id => "coordination-ref";
        public bool RepositoryBoundAdmission => true;
        public bool AdmissionFinality => true;
        public bool RequiresExternalPrerequisite => false;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            var response = server.Send(new WireRequest(
                "graphql-update-ref", invocation.OperationId, "coordination",
                invocation.ObservedCoordinationOid, invocation.SuccessorCoordinationOid,
                loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(new WireRequest(
                    "read-ref", invocation.OperationId, "coordination", null,
                    invocation.SuccessorCoordinationOid, LoseResponse: false));
            }
            return response.Outcome is "admitted" or "exact-recovered";
        }
    }

    private sealed class ManagedIssueLedgerAlternative : IAlternative
    {
        public string Id => "managed-issue-ledger";
        public bool RepositoryBoundAdmission => false;
        public bool AdmissionFinality => false;
        public bool RequiresExternalPrerequisite => false;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            if (!server.RepositoryPredecessorMatches(invocation))
            {
                server.Record("issue-gate", invocation.OperationId, "repository-mismatch");
                return false;
            }
            var response = server.Send(new WireRequest(
                "append-issue-comment", invocation.OperationId, "issue-ledger",
                invocation.ObservedCoordinationOid, null, loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(new WireRequest(
                    "read-issue-comments", invocation.OperationId, "issue-ledger",
                    invocation.ObservedCoordinationOid, null, LoseResponse: false));
            }
            var election = server.Send(new WireRequest(
                "elect-issue-operation", invocation.OperationId, "issue-ledger",
                invocation.ObservedCoordinationOid, null, LoseResponse: false));
            return response.Outcome is "admitted" or "exact-recovered"
                && election.Outcome == "admitted";
        }
    }

    private sealed class ExternalSerializerAlternative : IAlternative
    {
        public string Id => "external-serializer";
        public bool RepositoryBoundAdmission => false;
        public bool AdmissionFinality => true;
        public bool RequiresExternalPrerequisite => true;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            if (!server.RepositoryPredecessorMatches(invocation))
            {
                server.Record("serializer-gate", invocation.OperationId, "repository-mismatch");
                return false;
            }
            var response = server.Send(new WireRequest(
                "acquire-external-lease", invocation.OperationId, "serializer",
                invocation.ObservedCoordinationOid, null, loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(new WireRequest(
                    "read-external-lease", invocation.OperationId, "serializer",
                    invocation.ObservedCoordinationOid, null, LoseResponse: false));
            }
            return response.Outcome is "admitted" or "exact-recovered";
        }
    }

    private sealed class SerializedFaultServer
    {
        private readonly HashSet<string> issueComments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> content = new(StringComparer.Ordinal);
        private string? externalLeaseOwner;

        public string CoordinationOid { get; set; } = Oid('1');
        public string ProposalOid { get; set; } = Oid('2');
        public string TargetOid { get; set; } = Oid('3');
        public int ResourceWrites { get; private set; }
        public List<string> PullRequests { get; } = [];
        public List<string> Transcript { get; } = [];

        public bool RepositoryPredecessorMatches(Invocation invocation) =>
            CoordinationOid == invocation.ObservedCoordinationOid
            && TargetOid == invocation.ObservedTargetOid;

        public WireResponse Send(WireRequest request)
        {
            var json = JsonSerializer.Serialize(request);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(request.Kind, parsed.RootElement.GetProperty("Kind").GetString());
            var outcome = request.Kind switch
            {
                "graphql-update-ref" => UpdateRef(request),
                "read-ref" => ReadRef(request),
                "append-issue-comment" => AppendIssue(request),
                "read-issue-comments" => issueComments.Contains(request.OperationId)
                    ? "exact-recovered" : "not-found",
                "elect-issue-operation" => issueComments.Order(StringComparer.Ordinal).FirstOrDefault()
                    == request.OperationId ? "admitted" : "rejected",
                "acquire-external-lease" => AcquireLease(request),
                "read-external-lease" => externalLeaseOwner == request.OperationId
                    ? "exact-recovered" : "rejected",
                "create-content" => CreateContent(request),
                "read-content" => content.GetValueOrDefault(request.Resource) == request.AfterOid
                    ? "exact-recovered" : "not-found",
                "create-pr" => CreatePullRequest(request),
                "discover-pr" => PullRequests.Contains(request.OperationId, StringComparer.Ordinal)
                    ? "exact-recovered" : "not-found",
                _ => throw new InvalidOperationException("Unknown serialized request kind."),
            };
            var visible = request.LoseResponse && outcome is "admitted" or "exact-recovered"
                ? "response-lost"
                : outcome;
            Record("HTTP", json, visible);
            return new WireResponse(visible);
        }

        public void RecordExternal(string resource, string value) =>
            Record("external", resource, value);

        public void Record(string kind, string request, string response) =>
            Transcript.Add($"{kind} request={request} response={response}");

        private string UpdateRef(WireRequest request)
        {
            Assert.NotNull(request.BeforeOid);
            Assert.NotNull(request.AfterOid);
            Assert.NotEqual(Zero, request.AfterOid);
            var current = request.Resource == "coordination" ? CoordinationOid : ProposalOid;
            if (current == request.AfterOid)
            {
                return "exact-recovered";
            }
            if (current != request.BeforeOid)
            {
                return "rejected";
            }
            if (request.Resource == "coordination")
            {
                CoordinationOid = request.AfterOid;
            }
            else
            {
                ProposalOid = request.AfterOid;
                ResourceWrites++;
            }
            return "admitted";
        }

        private string ReadRef(WireRequest request)
        {
            var current = request.Resource == "coordination" ? CoordinationOid : ProposalOid;
            return current == request.AfterOid ? "exact-recovered" : "not-found";
        }

        private string AppendIssue(WireRequest request)
        {
            if (issueComments.Add(request.OperationId))
            {
                return "admitted";
            }
            return "exact-recovered";
        }

        private string AcquireLease(WireRequest request)
        {
            if (externalLeaseOwner is null)
            {
                externalLeaseOwner = request.OperationId;
                return "admitted";
            }
            return externalLeaseOwner == request.OperationId ? "exact-recovered" : "rejected";
        }

        private string CreateContent(WireRequest request)
        {
            if (!content.TryAdd(request.Resource, request.AfterOid!))
            {
                return content[request.Resource] == request.AfterOid
                    ? "exact-recovered" : "rejected";
            }
            ResourceWrites++;
            return "admitted";
        }

        private string CreatePullRequest(WireRequest request)
        {
            if (!PullRequests.Contains(request.OperationId, StringComparer.Ordinal))
            {
                PullRequests.Add(request.OperationId);
                ResourceWrites++;
                return "admitted";
            }
            return "exact-recovered";
        }
    }

    private sealed class Invocation
    {
        public Invocation(string operationId, SerializedFaultServer server)
        {
            OperationId = operationId;
            ObservedCoordinationOid = server.CoordinationOid;
            ObservedProposalOid = server.ProposalOid;
            ObservedTargetOid = server.TargetOid;
        }

        public string OperationId { get; }
        public string ObservedCoordinationOid { get; }
        public string ObservedProposalOid { get; }
        public string ObservedTargetOid { get; }
        public string SuccessorCoordinationOid => OperationId.EndsWith('a') ? Oid('4') : Oid('5');
        public string SuccessorProposalOid => OperationId.EndsWith('a') ? Oid('a') : Oid('b');
        public string ExpectedCommitOid => OperationId.EndsWith('a') ? Oid('c') : Oid('d');
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

    private static string Oid(char value) => new(value, 40);
    private const string Zero = "0000000000000000000000000000000000000000";

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
    private sealed record WireRequest(
        string Kind,
        string OperationId,
        string Resource,
        string? BeforeOid,
        string? AfterOid,
        bool LoseResponse);
    private sealed record WireResponse(string Outcome);
    private sealed record Metrics(
        int AdmittedOperations,
        int ResourceWrites,
        int PullRequestCount,
        string Residual,
        bool RepositoryBoundAdmission,
        bool AdmissionFinality,
        bool RequiresExternalPrerequisite);
    private sealed record Observation(
        string Alternative,
        string Scenario,
        bool RequirementSatisfied,
        int AdmittedOperations,
        int ResourceWrites,
        int PullRequestCount,
        string Residual,
        bool RepositoryBoundAdmission,
        bool AdmissionFinality,
        bool RequiresExternalPrerequisite,
        string Transcript);
}
