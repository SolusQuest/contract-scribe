using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Five_target_delayed_checkpoint_confirmation_reaches_actual_patch_PR_cleanup_and_Action(bool renew)
    {
        await using var fixture = await FiveTargetFixture("shared");
        string? reference = null, predecessor = null, expected = null;
        var armed = false;
        var injected = false;
        var reads = 0;
        GitHubProposalLoopbackServer.Request? pendingMutation = null;
        fixture.GitHub.BeforeBody = (request, body) =>
        {
            if (pendingMutation is not null || request.Kind != "checkpoint-commit") return;
            var before = request.Body!["variables"]!["input"]!["expectedHeadOid"]!.GetValue<string>();
            var prior = fixture.GitHub.DocumentAt(before);
            var next = GitHubProposalLoopbackServer.WrittenCanonicalFile(request, "coordination-state-v1.json")!;
            if (prior["checkpoint"] is null || next["publication"] is not null) return;
            var checkpoint = fixture.Checkpoint();
            var sameCheckpoint = JsonNode.DeepEquals(prior["checkpoint"], next["checkpoint"]);
            if (renew ? !sameCheckpoint || checkpoint.State.CandidateObservation is null || checkpoint.State.ScribeProgress?.Session?.Ended != true
                : sameCheckpoint || checkpoint.State.WorkItems.All(work => work.TrustedProposal is null)) return;
            reference = fixture.GitHub.Refs.Keys.Single(name => name.Contains("/coordination/", StringComparison.Ordinal));
            predecessor = before;
            pendingMutation = request;
        };
        fixture.GitHub.Before = request =>
        {
            if (!armed || injected || request.Method != "GET" || request.Path != "/repos/Owner/repo/git/ref/" + reference![5..]) return;
            // Model one stale REST observation, not a second mutation. Dispatch
            // snapshots this response before After restores the actual head.
            fixture.GitHub.Refs[reference] = predecessor!;
        };
        fixture.GitHub.After = request =>
        {
            if (request == pendingMutation && !injected)
            {
                // The signed OID comes from the actual wire database effect.
                expected = fixture.GitHub.Refs[reference!];
                armed = true;
            }
            if (expected is null) return;
            if (!armed || request.Method != "GET" || request.Path != "/repos/Owner/repo/git/ref/" + reference![5..]) return;
            reads++;
            if (!injected)
            {
                fixture.GitHub.Refs[reference] = expected;
                injected = true;
            }
        };
        var result = await fixture.Run();
        Assert.True(injected);
        Assert.True(reads >= 3); // predecessor, physical expected, independent final fence
        AssertResult(result, 0, "published");
        var checkpoint = fixture.Checkpoint();
        Assert.True(checkpoint.State.ScribeProgress!.Session!.Ended);
        Assert.Equal(5, checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        Assert.Equal(1, checkpoint.State.LineageCharges.PatchValidationInvocations);
        Assert.Null(checkpoint.State.ActiveReservation);
        Assert.Single(fixture.GitHub.PullRequests);
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.True(fixture.GitHub.CoordinationDocument()["claim"]!["expiresUnixSeconds"]!.GetValue<long>() <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        using var envelope = JsonDocument.Parse(result.Stdout);
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure").ValueKind);
        Assert.Equal(0, envelope.RootElement.GetProperty("invocationUsage").GetProperty("calls").GetProperty("retryDispatches").GetInt32());
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: "published");
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(403, "Permission")]
    [InlineData(404, "NotFound")]
    public async Task Five_target_publication_entry_failed_confirmation_stops_actual_CLI_cleanup(int status, string transport)
    {
        await using var fixture = await FiveTargetFixture("shared");
        var pending = false;
        var failureIndex = -1;
        string? expected = null;
        fixture.GitHub.BeforeBody = (request, body) =>
        {
            if (failureIndex >= 0 || request.Kind != "checkpoint-commit") return;
            var before = request.Body!["variables"]!["input"]!["expectedHeadOid"]!.GetValue<string>();
            var prior = fixture.GitHub.DocumentAt(before);
            var next = GitHubProposalLoopbackServer.WrittenCanonicalFile(request, "coordination-state-v1.json")!;
            if (prior["checkpoint"] is null || next["publication"] is not null
                || !JsonNode.DeepEquals(prior["checkpoint"], next["checkpoint"])) return;
            var checkpoint = fixture.Checkpoint();
            if (checkpoint.State.CandidateObservation is null || checkpoint.State.ScribeProgress?.Session?.Ended != true) return;
            pending = true;
        };
        fixture.GitHub.After = request =>
        {
            if (!pending || request.Kind != "checkpoint-commit") return;
            pending = false;
            expected = fixture.GitHub.Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal)).Value;
            failureIndex = fixture.GitHub.Requests.Count;
            fixture.GitHub.RejectPath = "/git/ref/heads/contract-scribe/coordination/";
            fixture.GitHub.RejectionStatus = status;
        };
        var result = await fixture.Run();
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotNull(expected);
        Assert.True(failureIndex > 0);
        Assert.DoesNotContain(fixture.GitHub.Requests.Skip(failureIndex), request => request.Mutation > 0);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        var checkpoint = fixture.Checkpoint();
        Assert.True(checkpoint.State.ScribeProgress!.Session!.Ended);
        Assert.Equal(5, checkpoint.State.CandidateObservation!.AcceptedWorkItemKeys.Length);
        Assert.Equal(1, checkpoint.State.LineageCharges.PatchValidationInvocations);
        Assert.Null(checkpoint.State.ActiveReservation);
        using var envelope = JsonDocument.Parse(result.Stdout);
        var failure = envelope.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal("Renew", failure.GetProperty("operation").GetString());
        Assert.Equal("FinalRef", failure.GetProperty("stage").GetString());
        Assert.Equal(transport, failure.GetProperty("transportCode").GetString());
        Assert.Equal(status, failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("cancellation").ValueKind);
        var comparison = failure.GetProperty("refComparison");
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes("contract-scribe.checkpoint.commit\0" + expected)));
        Assert.Equal(fingerprint, comparison.GetProperty("expectedCommit").GetString());
        Assert.Equal("Unavailable", comparison.GetProperty("relation").GetString());
        Assert.Equal("NeedsReadback", comparison.GetProperty("delivery").GetString());
        Assert.True(comparison.GetProperty("acknowledged").GetBoolean());
        Assert.DoesNotContain(expected!, result.Stdout, StringComparison.Ordinal);
        var outcome = envelope.RootElement.GetProperty("outcome").GetString()!["github-proposal.".Length..];
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, progressOutcome: outcome);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Actual_CLI_durable_member_failed_ack_reaches_Action_without_advancing_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var injected = false;
        long? durableRevision = null;
        fixture.GitHub.After = request =>
        {
            if (injected || request.Kind != "checkpoint-commit"
                || !fixture.GitHub.Refs.Keys.Any(key => key.Contains("/coordination/", StringComparison.Ordinal))
                || fixture.GitHub.CoordinationDocument()["checkpoint"] is null) return;
            var state = fixture.Checkpoint();
            if (!state.State.WorkItems.Any(work => work.TrustedProposal is not null)) return;
            injected = true;
            durableRevision = state.CheckpointRevision;
            fixture.GitHub.RejectPath = "/git/ref/heads/contract-scribe/coordination/";
            fixture.GitHub.RejectionStatus = 404;
        };
        var failed = await fixture.Run();
        Assert.True(injected);
        AssertResult(failed, 5, "host-failure");
        var current = fixture.Checkpoint();
        Assert.Equal(durableRevision, current.CheckpointRevision);
        Assert.Single(current.State.WorkItems, work => work.TrustedProposal is not null);
        Assert.NotNull(current.State.ActiveReservation);
        Assert.Null(current.State.TerminalOutcome);
        Assert.Equal(0, current.State.LineageCharges.PatchValidationInvocations);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        var requests = fixture.Provider.RequestCount;
        using var envelope = JsonDocument.Parse(failed.Stdout);
        Assert.Equal(current.CheckpointRevision - 1, envelope.RootElement.GetProperty("checkpointRevision").GetInt64());
        var failure = envelope.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal("Write", failure.GetProperty("operation").GetString());
        Assert.Equal(current.CheckpointRevision, failure.GetProperty("attemptedRevision").GetInt64());
        Assert.Equal("FinalRef", failure.GetProperty("stage").GetString());
        Assert.Equal("RefMismatch", failure.GetProperty("reason").GetString());
        Assert.Equal("NotFound", failure.GetProperty("transportCode").GetString());
        Assert.Equal(404, failure.GetProperty("httpStatus").GetInt32());
        var comparison = failure.GetProperty("refComparison");
        Assert.Equal("Unavailable", comparison.GetProperty("relation").GetString());
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("observedCommit").ValueKind);
        Assert.Equal("NeedsReadback", comparison.GetProperty("delivery").GetString());
        Assert.True(comparison.GetProperty("acknowledged").GetBoolean());
        Assert.Equal(64, comparison.GetProperty("expectedCommit").GetString()!.Length);
        await AssertActualActionConsumer(fixture.Outside, failed.Stdout, failed.ExitCode, checkpoint: true);
        Assert.Equal(requests, fixture.Provider.RequestCount);
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Actual_CLI_semantic_prefix_and_terminal_HTTP_status_reach_the_Action_without_more_effects()
    {
        await using var fixture = await Fixture.CreateAsync(providerScenario: "semantic");
        fixture.Provider.PaymentRequiredAt = 2;
        var failed = await fixture.Run();
        AssertResult(failed, 5, "host-failure");
        Assert.Equal(2, fixture.Provider.RequestCount);
        var current = fixture.Checkpoint();
        Assert.Null(current.State.ActiveReservation);
        Assert.Equal(2, current.State.DispatchObservations.Length);
        Assert.Equal(DocumentationScribeDispatchDisposition.Success, current.State.DispatchObservations[0].Disposition);
        Assert.Equal(31, current.State.DispatchObservations[0].Usage!.InputTokens);
        Assert.Equal(DocumentationScribeDispatchDisposition.TerminalFailure, current.State.DispatchObservations[1].Disposition);
        Assert.Null(current.State.DispatchObservations[1].Usage);
        Assert.True(current.State.DispatchObservations[1].Charges!.InputTokens.ConservativeUnobserved > 0);
        Assert.Empty(fixture.GitHub.PullRequests);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.Equal(0, current.State.LineageCharges.PatchValidationInvocations);
        var usage = EvidenceUsage(failed);
        Assert.Equal(0, usage.GetProperty("calls").GetProperty("retryDispatches").GetInt32());
        Assert.Equal(1, usage.GetProperty("calls").GetProperty("toolCalls").GetInt32());
        var diagnostics = usage.GetProperty("providerDiagnostics");
        Assert.Equal(0, diagnostics.GetProperty("missingDispatches").GetInt32());
        var rows = diagnostics.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal("codec.accepted-tool", rows[0].GetProperty("codecDisposition").GetString());
        Assert.Equal(2, rows[1].GetProperty("physicalDispatchOrdinal").GetInt32());
        Assert.Equal("model.failure.permanent-unavailable", rows[1].GetProperty("failureCode").GetString());
        Assert.Equal("model.failure-origin.http-status", rows[1].GetProperty("failureOrigin").GetString());
        Assert.Equal(402, rows[1].GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("codecDisposition").ValueKind);
        using var envelope = JsonDocument.Parse(failed.Stdout);
        Assert.Equal(current.CheckpointRevision, envelope.RootElement.GetProperty("checkpointRevision").GetInt64());
        await AssertActualActionConsumer(fixture.Outside, failed.Stdout, failed.ExitCode);
        await fixture.AssertSourceUnchanged();
    }

    private static async Task AssertActualActionConsumer(string outside, string stdout, int exit, bool checkpoint = false,
        bool publication = false, bool successful = false, string? progressOutcome = null, string? actualStderr = null)
    {
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import base64, json, os, sys
            sys.dont_write_bytecode = True
            sys.path.insert(0, sys.argv[1])
            import invoke, usage, common
            data = sys.stdin.buffer.read()
            assert len(data) <= common.BOUND_ENVELOPE
            parsed = invoke.parse_envelope(data, base64.b64decode(sys.argv[6]), int(sys.argv[3]))
            os.environ['GITHUB_OUTPUT'] = sys.argv[2]
            invoke.emit_outputs(parsed, data, int(sys.argv[3]))
            rows = usage.summary_rows(parsed['invocationUsage'])
            if sys.argv[4] == 'progress':
                assert parsed['outcome'] == 'github-proposal.' + sys.argv[5]
                assert usage.valid(parsed['invocationUsage'])
            elif sys.argv[4] == 'successful':
                assert parsed['outcome'] == 'github-proposal.published'
                assert parsed['publicationDiagnostic'] is None
                assert parsed['invocationUsage']['calls']['activeSent'] == 0
                assert parsed['invocationUsage']['calls']['activeReserved'] == 0
            elif sys.argv[4] == 'publication':
                observation = parsed['publicationDiagnostic']
                assert observation['boundary'] == 'CoordinationAdvanceRef'
                assert observation['owner'] == 'Coordination'
                assert observation['transportHttpStatus'] in (403, 404)
                assert observation['transportCode'] == ('Permission' if observation['transportHttpStatus'] == 403 else 'NotFound')
                assert parsed['invocationUsage']['calls']['activeSent'] == 0
                assert parsed['invocationUsage']['calls']['activeReserved'] == 0
                wire = json.dumps(observation, separators=(',', ':'))
                assert ('publication-diagnostic=' + wire) in open(sys.argv[2]).read()
            elif sys.argv[4] == 'checkpoint':
                assert any('FinalRef / RefMismatch' in row for row in rows)
                assert any('NotFound / 404' in row for row in rows)
                assert parsed['invocationUsage']['checkpointFailure']['attemptedRevision'] > parsed['checkpointRevision']
                comparison = parsed['invocationUsage']['checkpointFailure']['refComparison']
                assert comparison['relation'] == 'Unavailable' and comparison['observedCommit'] is None
                assert comparison['delivery'] == 'NeedsReadback' and comparison['acknowledged'] is True
                assert any('Unavailable / NeedsReadback / yes' in row for row in rows)
            else:
                assert any('402' in row and 'model.failure.permanent-unavailable' in row for row in rows)
            summary = '\n'.join(rows)
            for row in parsed['invocationUsage']['providerDiagnostics']['observations']:
                assert row['attemptId'] not in summary
            assert 'synthetic-semantic-read' not in summary
            print('closed-consumer-ok')
            """);
        start.ArgumentList.Add(Path.Join(CampaignCliProcessTests.RepositoryRoot, "scripts", "action"));
        start.ArgumentList.Add(Path.Join(outside, "actual-action-outputs.txt"));
        start.ArgumentList.Add(exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(progressOutcome is not null ? "progress" : successful ? "successful" : publication ? "publication" : checkpoint ? "checkpoint" : "provider");
        start.ArgumentList.Add(progressOutcome ?? "");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(actualStderr ?? "")));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(stdout);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("closed-consumer-ok", (await output).Trim());
            Assert.Equal("", await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
