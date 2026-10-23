using System.Diagnostics;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Actual_CLI_durable_member_failed_ack_reaches_Action_without_advancing_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var injected = false;
        long? durableRevision = null;
        fixture.GitHub.After = request =>
        {
            if (injected || request.Method != "POST" || request.Path != "/graphql"
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

    private static async Task AssertActualActionConsumer(string outside, string stdout, int exit, bool checkpoint = false, bool publication = false)
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
            import json, os, sys
            sys.dont_write_bytecode = True
            sys.path.insert(0, sys.argv[1])
            import invoke, usage, common
            data = sys.stdin.buffer.read()
            assert len(data) <= common.BOUND_ENVELOPE
            parsed = invoke.parse_envelope(data, b'', int(sys.argv[3]))
            os.environ['GITHUB_OUTPUT'] = sys.argv[2]
            invoke.emit_outputs(parsed, data, int(sys.argv[3]))
            rows = usage.summary_rows(parsed['invocationUsage'])
            if sys.argv[4] == 'publication':
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
        start.ArgumentList.Add(publication ? "publication" : checkpoint ? "checkpoint" : "provider");
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
