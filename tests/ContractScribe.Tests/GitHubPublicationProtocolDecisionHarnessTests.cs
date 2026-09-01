using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationProtocolDecisionHarnessTests
{
    private static readonly Scenario[] Scenarios =
    [
        new("initial-create-response-loss", Fault.InitialClaimResponseLoss),
        new("stale-after-final-read", Fault.StaleAfterFinalRead),
        new("coordination-rewind-after-final-read", Fault.CoordinationRewindAfterFinalRead),
        new("proposal-rewind-after-final-read", Fault.ProposalRewindAfterFinalRead),
        new("target-move-before-claim", Fault.TargetMoveBeforeClaim),
        new("target-move-after-claim-before-resource", Fault.TargetMoveBeforeResource),
        new("target-move-during-pr-create", Fault.TargetMoveDuringPullRequestCreate),
        new("two-first-publication-invocations", Fault.TwoInitialInvocations),
        new("two-append-invocations", Fault.TwoAppendInvocations),
        new("ambiguous-commit-response", Fault.CommitResponseLoss),
        new("ambiguous-proposal-ref-response", Fault.ProposalRefResponseLoss),
        new("ambiguous-pr-response", Fault.PullRequestResponseLoss),
    ];

    [Fact]
    public void Execute_three_alternatives_at_equivalent_serialized_race_windows()
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
        Assert.Contains(results, result => result.Alternative == "managed-issue-ledger"
            && result.Scenario == "two-first-publication-invocations"
            && !result.RequirementSatisfied
            && !result.AdmissionFinality);
        Assert.Contains(results, result => result.Alternative == "external-serializer"
            && result.Scenario == "stale-after-final-read"
            && !result.RequirementSatisfied
            && result.AdmittedOperations == 1);
        Assert.Contains(results, result => result.Scenario == "target-move-during-pr-create"
            && result.PullRequestCount == 1
            && result.Residual == "one-marker-owned-stale-draft");

        var root = FindRepositoryRoot();
        var output = Path.Join(root, "TestResults", "issue-158-protocol-decision-v3",
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
        if (scenario.Fault == Fault.TargetMoveBeforeClaim)
        {
            server.TargetOid = Oid('9');
            server.RecordExternal("target-before-final-read", server.TargetOid);
        }

        var first = new Invocation("operation-b", server.CoordinationOid, Oid('2'), Oid('3'));
        var second = new Invocation("operation-c", server.CoordinationOid, Oid('2'), Oid('3'));
        server.AdmissionFault = scenario.Fault;

        var firstAdmitted = alternative.Admit(
            server,
            first,
            loseResponse: scenario.Fault == Fault.InitialClaimResponseLoss);
        var admitted = firstAdmitted ? 1 : 0;
        if (scenario.Fault == Fault.InitialClaimResponseLoss && firstAdmitted
            && alternative.Admit(server, first, loseResponse: false))
        {
            server.Record("replay", first.OperationId, "exact-readback");
        }
        if (scenario.Fault is Fault.TwoInitialInvocations or Fault.TwoAppendInvocations
            && alternative.Admit(server, second, loseResponse: false))
        {
            admitted++;
        }

        var residual = "none";
        if (firstAdmitted && scenario.Fault == Fault.ProposalRewindAfterFinalRead)
        {
            var read = server.Send(alternative.RepositoryBoundAdmission
                ? WireRequest.ReadClaimAndBase(first)
                : WireRequest.ReadRepository(first));
            Assert.Equal("exact", read.Outcome);
            var updated = server.Send(WireRequest.UpdateProposal(first));
            residual = updated.Outcome == "rejected" ? "proposal-conflict" : "unexpected-write";
        }
        else if (firstAdmitted && scenario.Fault == Fault.TargetMoveBeforeResource)
        {
            server.TargetOid = Oid('9');
            server.RecordExternal("target-before-resource", server.TargetOid);
            var read = server.Send(alternative.RepositoryBoundAdmission
                ? WireRequest.ReadClaimAndBase(first)
                : WireRequest.ReadRepository(first));
            residual = read.Outcome == "mismatch" ? "zero-resource-mutation" : "unexpected-write";
        }
        else if (firstAdmitted && scenario.Fault == Fault.TargetMoveDuringPullRequestCreate)
        {
            var finalRead = alternative.RepositoryBoundAdmission
                ? WireRequest.ReadClaimAndBase(first)
                : WireRequest.ReadRepository(first);
            Assert.Equal("exact", server.Send(finalRead).Outcome);
            server.MoveTargetDuringNextPullRequestCreate = true;
            server.Send(WireRequest.CreatePullRequest(first,
                loseResponse: false));
            var discovered = server.Send(WireRequest.DiscoverPullRequest(first));
            residual = discovered.Outcome == "stale-base-after-create"
                ? "one-marker-owned-stale-draft" : discovered.Outcome;
        }
        else if (firstAdmitted && scenario.Fault == Fault.CommitResponseLoss)
        {
            server.Send(WireRequest.CreateContent(first, loseResponse: true));
            residual = server.Send(WireRequest.ReadContent(first)).Outcome;
        }
        else if (firstAdmitted && scenario.Fault == Fault.ProposalRefResponseLoss)
        {
            server.Send(WireRequest.UpdateProposal(first, loseResponse: true));
            residual = server.Send(WireRequest.ReadProposal(first)).Outcome;
        }
        else if (firstAdmitted && scenario.Fault == Fault.PullRequestResponseLoss)
        {
            server.Send(WireRequest.CreatePullRequest(first, loseResponse: true));
            residual = server.Send(WireRequest.DiscoverPullRequest(first)).Outcome;
        }

        var finality = server.DeriveFinality(alternative.Id, first.OperationId);
        var metrics = new Metrics(
            admitted,
            server.ResourceWrites,
            server.PullRequests.Count,
            residual,
            alternative.RepositoryBoundAdmission,
            finality,
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
        Fault.TargetMoveBeforeClaim =>
            metrics.AdmittedOperations == 0 && metrics.ResourceWrites == 0,
        Fault.StaleAfterFinalRead or Fault.CoordinationRewindAfterFinalRead =>
            metrics.AdmittedOperations == 0 && metrics.ResourceWrites == 0,
        Fault.ProposalRewindAfterFinalRead =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "proposal-conflict",
        Fault.TargetMoveBeforeResource =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "zero-resource-mutation",
        Fault.TargetMoveDuringPullRequestCreate =>
            metrics.PullRequestCount == 1 && metrics.Residual == "one-marker-owned-stale-draft",
        Fault.TwoInitialInvocations or Fault.TwoAppendInvocations =>
            metrics.AdmittedOperations == 1 && metrics.AdmissionFinality,
        Fault.CommitResponseLoss or Fault.ProposalRefResponseLoss =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "exact-recovered",
        Fault.PullRequestResponseLoss =>
            metrics.AdmittedOperations == 1 && metrics.Residual == "exact-recovered",
        Fault.InitialClaimResponseLoss =>
            metrics.AdmittedOperations == 1 && metrics.AdmissionFinality,
        _ => false,
    };

    private interface IAlternative
    {
        string Id { get; }
        bool RepositoryBoundAdmission { get; }
        bool RequiresExternalPrerequisite { get; }
        bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse);
    }

    private sealed class CoordinationRefAlternative : IAlternative
    {
        public string Id => "coordination-ref";
        public bool RepositoryBoundAdmission => true;
        public bool RequiresExternalPrerequisite => false;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            if (server.Send(WireRequest.ReadRepository(invocation)).Outcome != "exact") return false;
            var response = server.Send(WireRequest.UpdateCoordination(invocation, loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(WireRequest.ReadCoordination(invocation));
            }
            return response.Outcome is "admitted" or "exact-recovered";
        }
    }

    private sealed class ManagedIssueLedgerAlternative : IAlternative
    {
        public string Id => "managed-issue-ledger";
        public bool RepositoryBoundAdmission => false;
        public bool RequiresExternalPrerequisite => false;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            if (server.Send(WireRequest.ReadRepository(invocation)).Outcome != "exact") return false;
            var response = server.Send(WireRequest.AppendIssue(invocation, loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(WireRequest.ReadIssue(invocation));
            }
            var election = server.Send(WireRequest.ElectIssue(invocation));
            return response.Outcome is "admitted" or "exact-recovered"
                && election.Outcome == "admitted";
        }
    }

    private sealed class ExternalSerializerAlternative : IAlternative
    {
        public string Id => "external-serializer";
        public bool RepositoryBoundAdmission => false;
        public bool RequiresExternalPrerequisite => true;

        public bool Admit(SerializedFaultServer server, Invocation invocation, bool loseResponse)
        {
            if (server.Send(WireRequest.ReadRepository(invocation)).Outcome != "exact") return false;
            var response = server.Send(WireRequest.AcquireLease(invocation, loseResponse));
            if (response.Outcome == "response-lost")
            {
                response = server.Send(WireRequest.ReadLease(invocation));
            }
            return response.Outcome is "admitted" or "exact-recovered";
        }
    }

    private sealed class SerializedFaultServer
    {
        private readonly HashSet<string> issueClaims = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> content = new(StringComparer.Ordinal);
        private string? externalLeaseOwner;
        private bool admissionFaultInjected;

        public string CoordinationOid { get; set; } = Oid('1');
        public string ProposalOid { get; set; } = Oid('2');
        public string TargetOid { get; set; } = Oid('3');
        public Fault AdmissionFault { get; set; }
        public bool MoveTargetDuringNextPullRequestCreate { get; set; }
        public int ResourceWrites { get; private set; }
        public List<PullRequestRecord> PullRequests { get; } = [];
        public List<string> Transcript { get; } = [];

        public WireResponse Send(WireRequest request)
        {
            var json = JsonSerializer.Serialize(request);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(request.Kind, parsed.RootElement.GetProperty("Kind").GetString());
            if (request.Kind is "graphql-update-ref" or "append-issue-comment"
                or "acquire-external-lease")
            {
                InjectAdmissionFault(request);
            }
            var outcome = request.Kind switch
            {
                "read-repository" => ReadRepository(request),
                "graphql-update-ref" => UpdateRef(request),
                "read-ref" => ReadRef(request),
                "append-issue-comment" => AppendIssue(request),
                "read-issue-comments" => issueClaims.Contains(request.OperationId)
                    ? "exact-recovered" : "not-found",
                "elect-issue-operation" => issueClaims.Order(StringComparer.Ordinal).FirstOrDefault()
                    == request.OperationId ? "admitted" : "rejected",
                "acquire-external-lease" => AcquireLease(request),
                "read-external-lease" => externalLeaseOwner == request.OperationId
                    ? "exact-recovered" : "rejected",
                "create-content" => CreateContent(request),
                "read-content" => content.GetValueOrDefault(request.Resource) == request.AfterOid
                    ? "exact-recovered" : "not-found",
                "create-pr" => CreatePullRequest(request),
                "discover-pr" => DiscoverPullRequest(request),
                _ => throw new InvalidOperationException("Unknown serialized request kind."),
            };
            var visible = request.LoseResponse && outcome is "admitted" or "exact-recovered"
                ? "response-lost" : outcome;
            Record("HTTP", json, visible);
            return new WireResponse(visible);
        }

        public bool DeriveFinality(string alternative, string operationId)
        {
            if (alternative == "managed-issue-ledger")
            {
                var before = issueClaims.Order(StringComparer.Ordinal).FirstOrDefault();
                issueClaims.Add("operation-a");
                Record("external", "late-issue-claim", "operation-a");
                var after = issueClaims.Order(StringComparer.Ordinal).FirstOrDefault();
                return before == operationId && after == operationId;
            }
            if (alternative == "external-serializer")
            {
                return externalLeaseOwner == operationId;
            }
            return CoordinationOid == SuccessorCoordination(operationId);
        }

        public void RecordExternal(string resource, string value) =>
            Record("external", resource, value);
        public void Record(string kind, string request, string response) =>
            Transcript.Add($"{kind} request={request} response={response}");

        private void InjectAdmissionFault(WireRequest request)
        {
            if (admissionFaultInjected) return;
            if (AdmissionFault == Fault.StaleAfterFinalRead)
            {
                CoordinationOid = Oid('8');
                RecordExternal("stale-after-final-read", CoordinationOid);
                admissionFaultInjected = true;
            }
            else if (AdmissionFault == Fault.CoordinationRewindAfterFinalRead)
            {
                CoordinationOid = Oid('7');
                RecordExternal("rewind-after-final-read", CoordinationOid);
                admissionFaultInjected = true;
            }
            else if (AdmissionFault == Fault.ProposalRewindAfterFinalRead
                && request.Resource == "proposal")
            {
                ProposalOid = Oid('6');
                RecordExternal("proposal-rewind-after-final-read", ProposalOid);
                admissionFaultInjected = true;
            }
        }

        private string ReadRepository(WireRequest request) =>
            CoordinationOid == request.BeforeOid && TargetOid == request.ExpectedBaseOid
                ? "exact" : "mismatch";

        private string UpdateRef(WireRequest request)
        {
            Assert.NotNull(request.BeforeOid);
            Assert.NotNull(request.AfterOid);
            Assert.NotEqual(Zero, request.AfterOid);
            var current = request.Resource == "coordination" ? CoordinationOid : ProposalOid;
            if (current == request.AfterOid) return "exact-recovered";
            if (current != request.BeforeOid) return "rejected";
            if (request.Resource == "coordination") CoordinationOid = request.AfterOid;
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

        private string AppendIssue(WireRequest request) =>
            issueClaims.Add(request.OperationId) ? "admitted" : "exact-recovered";

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
            if (MoveTargetDuringNextPullRequestCreate)
            {
                TargetOid = Oid('9');
                MoveTargetDuringNextPullRequestCreate = false;
                RecordExternal("target-during-pr-create", TargetOid);
            }
            if (PullRequests.All(pr => pr.OperationId != request.OperationId))
            {
                PullRequests.Add(new PullRequestRecord(
                    request.OperationId, request.ExpectedBaseOid!, TargetOid));
                ResourceWrites++;
                return "admitted";
            }
            return "exact-recovered";
        }

        private string DiscoverPullRequest(WireRequest request)
        {
            var found = PullRequests.SingleOrDefault(pr => pr.OperationId == request.OperationId);
            if (found is null) return "not-found";
            return found.ExpectedBaseOid == found.ObservedBaseOid
                ? "exact-recovered" : "stale-base-after-create";
        }
    }

    private sealed record Invocation(
        string OperationId,
        string ObservedCoordinationOid,
        string ObservedProposalOid,
        string ObservedTargetOid)
    {
        public string SuccessorCoordinationOid => SuccessorCoordination(OperationId);
        public string SuccessorProposalOid => OperationId.EndsWith('b') ? Oid('a') : Oid('b');
        public string ExpectedCommitOid => OperationId.EndsWith('b') ? Oid('c') : Oid('d');
    }

    private sealed record WireRequest(
        string Kind,
        string OperationId,
        string Resource,
        string? BeforeOid,
        string? AfterOid,
        string? ExpectedBaseOid,
        bool LoseResponse)
    {
        public static WireRequest ReadRepository(Invocation i) =>
            new("read-repository", i.OperationId, "repository",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, false);
        public static WireRequest ReadClaimAndBase(Invocation i) =>
            new("read-repository", i.OperationId, "repository",
                i.SuccessorCoordinationOid, null, i.ObservedTargetOid, false);
        public static WireRequest UpdateCoordination(Invocation i, bool loseResponse) =>
            new("graphql-update-ref", i.OperationId, "coordination",
                i.ObservedCoordinationOid, i.SuccessorCoordinationOid, i.ObservedTargetOid, loseResponse);
        public static WireRequest ReadCoordination(Invocation i) =>
            new("read-ref", i.OperationId, "coordination", null,
                i.SuccessorCoordinationOid, i.ObservedTargetOid, false);
        public static WireRequest UpdateProposal(Invocation i, bool loseResponse = false) =>
            new("graphql-update-ref", i.OperationId, "proposal",
                i.ObservedProposalOid, i.SuccessorProposalOid, i.ObservedTargetOid, loseResponse);
        public static WireRequest ReadProposal(Invocation i) =>
            new("read-ref", i.OperationId, "proposal", null,
                i.SuccessorProposalOid, i.ObservedTargetOid, false);
        public static WireRequest AppendIssue(Invocation i, bool loseResponse) =>
            new("append-issue-comment", i.OperationId, "issue-ledger",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, loseResponse);
        public static WireRequest ReadIssue(Invocation i) =>
            new("read-issue-comments", i.OperationId, "issue-ledger",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, false);
        public static WireRequest ElectIssue(Invocation i) =>
            new("elect-issue-operation", i.OperationId, "issue-ledger",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, false);
        public static WireRequest AcquireLease(Invocation i, bool loseResponse) =>
            new("acquire-external-lease", i.OperationId, "serializer",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, loseResponse);
        public static WireRequest ReadLease(Invocation i) =>
            new("read-external-lease", i.OperationId, "serializer",
                i.ObservedCoordinationOid, null, i.ObservedTargetOid, false);
        public static WireRequest CreateContent(Invocation i, bool loseResponse) =>
            new("create-content", i.OperationId, "commit", null,
                i.ExpectedCommitOid, i.ObservedTargetOid, loseResponse);
        public static WireRequest ReadContent(Invocation i) =>
            new("read-content", i.OperationId, "commit", null,
                i.ExpectedCommitOid, i.ObservedTargetOid, false);
        public static WireRequest CreatePullRequest(Invocation i, bool loseResponse) =>
            new("create-pr", i.OperationId, "pull-request", null, null,
                i.ObservedTargetOid, loseResponse);
        public static WireRequest DiscoverPullRequest(Invocation i) =>
            new("discover-pr", i.OperationId, "pull-request", null, null,
                i.ObservedTargetOid, false);
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

    private static string SuccessorCoordination(string operationId) =>
        operationId.EndsWith('b') ? Oid('4') : Oid('5');
    private static string Oid(char value) => new(value, 40);
    private const string Zero = "0000000000000000000000000000000000000000";

    private enum Fault
    {
        InitialClaimResponseLoss,
        StaleAfterFinalRead,
        CoordinationRewindAfterFinalRead,
        ProposalRewindAfterFinalRead,
        TargetMoveBeforeClaim,
        TargetMoveBeforeResource,
        TargetMoveDuringPullRequestCreate,
        TwoInitialInvocations,
        TwoAppendInvocations,
        CommitResponseLoss,
        ProposalRefResponseLoss,
        PullRequestResponseLoss,
    }

    private sealed record Scenario(string Name, Fault Fault);
    private sealed record WireResponse(string Outcome);
    private sealed record PullRequestRecord(
        string OperationId,
        string ExpectedBaseOid,
        string ObservedBaseOid);
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
