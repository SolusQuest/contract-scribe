using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using ContractScribe.Cli;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("OperationCheckpointAcceptanceDeadline")]
    [InlineData("OperationSettlementToken")]
    public void Specific_cancellation_observations_are_private_operation_local_and_closed(string name)
    {
        var source = Enum.Parse<GitHubCheckpointCancellation>(name);
        using var owned = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        owned.Cancel();
        var opaque = new GitHubCheckpointDiagnostic(7, GitHubCheckpointOperation.Write, 17,
            GitHubCheckpointStage.CheckpointReadback, GitHubCheckpointReason.Cancelled,
            Cancellation: GitHubCheckpointCancellation.OperationToken);
        var bound = opaque.WithOperationCancellation(owned.Token);
        Assert.False(opaque.MatchesOperationCancellation(owned.Token));
        Assert.True(bound.MatchesOperationCancellation(owned.Token));
        Assert.False(bound.MatchesOperationCancellation(other.Token));
        Assert.False(bound.MatchesOperationCancellation(default));
        var valid = bound with { Cancellation = source };
        Assert.True(valid.IsValid);
        foreach (var invalid in new[] { valid with { Operation = GitHubCheckpointOperation.Read },
            valid with { Stage = GitHubCheckpointStage.FinalRef }, valid with { AttemptedRevision = null },
            valid with { Reason = GitHubCheckpointReason.Transport } }) Assert.False(invalid.IsValid);
        Assert.DoesNotContain("observedOperationCancellation", bound.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken", JsonSerializer.Serialize(bound), StringComparison.Ordinal);
        var usage = new InvocationUsage();
        usage.ObserveCheckpoint(valid);
        var terminal = GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.cancelled", 16));
        using var json = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(terminal, usage).StandardOutput);
        var projected = json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.Equal(9, projected.EnumerateObject().Count());
        Assert.Equal(source.ToString(), projected.GetProperty("cancellation").GetString());
        Assert.DoesNotContain("CancellationToken", projected.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_checkpoint_observations_cannot_replace_execution_or_escape_the_closed_report()
    {
        var valid = new GitHubCheckpointDiagnostic(1, GitHubCheckpointOperation.Write, 17,
            GitHubCheckpointStage.FinalRef, GitHubCheckpointReason.RefMismatch);
        GitHubCheckpointDiagnostic[] invalid = [valid with { OperationOrdinal = 0 }, valid with { AttemptedRevision = -1 },
            valid with { Operation = (GitHubCheckpointOperation)999 }, valid with { Stage = (GitHubCheckpointStage)999 },
            valid with { Reason = (GitHubCheckpointReason)999 }, valid with { TransportCode = (GitHubFailureCode)999 },
            valid with { HttpStatus = 404 }, valid with { TransportCode = GitHubFailureCode.NotFound, HttpStatus = 600 },
            valid with { Cancellation = GitHubCheckpointCancellation.Unknown }, valid with { Reason = GitHubCheckpointReason.Cancelled }];
        var terminal = GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.state-publication-failure", 16));
        foreach (var rejected in invalid)
        {
            Assert.False(rejected.IsValid);
            var usage = new InvocationUsage();
            usage.ObserveCheckpoint(rejected);
            var result = GitHubProposalPresentation.WithUsage(terminal, usage);
            Assert.Equal(terminal.ExitCode, result.ExitCode);
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure").ValueKind);
        }
    }

    [Fact]
    public async Task Invalid_canonical_checkpoint_observer_retains_validation_without_fabricated_transport()
    {
        var remote = new CoordinationRemote();
        using var client = CheckpointClient(remote);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var bytes = "{}"u8.ToArray();
        var oid = remote.SeedBlob(bytes);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        remote.AppendDocument(d => d with { Checkpoint = new(oid, sha, d.Checkpoint!.Revision + 1) });
        var read = await store.ReadAsync(default);
        Assert.Equal(CampaignCheckpointReadKind.Invalid, read.Kind);
        var observation = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
        Assert.Equal(GitHubCheckpointOperation.Read, observation.Operation);
        Assert.Equal(GitHubCheckpointReason.InvalidData, observation.Reason);
        Assert.Null(observation.AttemptedRevision);
        Assert.Null(observation.TransportCode);
        Assert.Null(observation.HttpStatus);
    }

    [Theory]
    [InlineData("clean", "Accepted", null)]
    [InlineData("lost-ack", "Accepted", null)]
    [InlineData("caller-pre", "Cancelled", "OperationToken")]
    [InlineData("caller-post", "Cancelled", "OperationToken")]
    [InlineData("expiry", "PublicationFailure", null)]
    [InlineData("owner", "PublicationFailure", null)]
    [InlineData("finalization", "PublicationFailure", "RecoveryToken")]
    public async Task Checkpoint_observation_preserves_existing_acceptance_and_cancellation(string fault, string expected, string? cancellation)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled);
        using var caller = new CancellationTokenSource();
        using var finalization = new CancellationTokenSource();
        if (fault == "finalization") client.BindFinalizationDeadline(finalization.Token);
        var mutations = remote.RefMutationAttempts;
        var injected = false;
        handler.AfterResponse = (request, response) =>
        {
            if (!injected && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/graphql")
            {
                injected = true;
                if (fault == "lost-ack") throw new HttpRequestException("private-transport-sentinel");
                if (fault == "caller-post") caller.Cancel();
                if (fault == "finalization") finalization.Cancel();
                if (fault == "expiry") remote.ServerNow = remote.ServerNow.AddSeconds(400);
                if (fault == "owner") remote.AppendDocument(d => d with
                { Claim = d.Claim! with { Owner = new string('f', 64), Epoch = d.Claim.Epoch + 1 } });
            }
            return Task.FromResult(response);
        };
        if (fault == "caller-pre") caller.Cancel();
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, next, caller.Token);
        Assert.Equal(expected, accepted.Kind.ToString());
        Assert.Equal(mutations + (fault == "caller-pre" ? 0 : 1), remote.RefMutationAttempts);
        if (expected == "Accepted")
        {
            Assert.Equal(next.Artifact.Sha256, accepted.Artifact!.Sha256);
            Assert.Null(store.CheckpointDiagnostic);
        }
        else
        {
            Assert.Null(accepted.AcceptedCheckpoint);
            var observation = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
            Assert.True(observation.IsValid);
            Assert.Equal(cancellation, observation.Cancellation?.ToString());
            if (fault == "expiry") Assert.Equal(GitHubCheckpointReason.LeaseUnverifiable, observation.Reason);
            if (fault == "owner") Assert.Equal(GitHubCheckpointReason.RefMismatch, observation.Reason);
            if (cancellation is not null) Assert.Equal(GitHubCheckpointReason.Cancelled, observation.Reason);
        }
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("permission")]
    [InlineData("exception")]
    public async Task Failed_checkpoint_retains_closed_first_post_CAS_observation(string fault)
    {
        var remote = new CoordinationRemote();
        using var handler = new CheckpointInterceptHandler(remote);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        SeedPublicationCheckpoint(remote);
        var before = PublicationCheckpointFixture("campaign-1");
        var next = CampaignStateReducer.Stop(before, CampaignTerminalKind.Cancelled);
        var oldHead = remote.CoordinationHead!;
        var blob = GitHubCoordinationObjects.ObjectOid("blob", next.Artifact.ExactUtf8Json.AsSpan());
        var cas = false;
        var injected = false;
        var predecessorObservations = 0;
        handler.AfterResponse = (request, response) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/graphql") cas = true;
            if (cas && (!injected || fault == "ref") && (fault == "ref"
                ? path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal)
                : path.EndsWith("/git/blobs/" + blob, StringComparison.Ordinal)))
            {
                injected = true;
                if (fault == "exception") throw new InvalidOperationException("private-error-body-key-sentinel");
                if (fault is "missing" or "permission")
                {
                    response.Dispose();
                    return Task.FromResult(new HttpResponseMessage(fault == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden));
                }
                var node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                // Under B a single predecessor is pending, so the negative
                // first-closed-failure control must exhaust all three reads.
                if (fault == "ref") { node["object"]!["sha"] = oldHead; predecessorObservations++; }
                else node["content"] = Convert.ToBase64String("{}"u8);
                response.Content.Dispose();
                response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        };
        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(store, next);
        Assert.True(injected);
        if (fault == "ref") Assert.Equal(3, predecessorObservations);
        Assert.Equal(CampaignCheckpointAcceptanceKind.PublicationFailure, accepted.Kind);
        Assert.Null(accepted.AcceptedCheckpoint);
        Assert.True(store.CheckpointToken.IsCancellationRequested);
        var observation = Assert.IsType<GitHubCheckpointDiagnostic>(store.CheckpointDiagnostic);
        Assert.True(observation.IsValid);
        Assert.Equal(GitHubCheckpointOperation.Write, observation.Operation);
        Assert.Equal(next.Artifact.CheckpointRevision, observation.AttemptedRevision);
        Assert.Equal(fault == "ref" ? GitHubCheckpointStage.FinalRef : GitHubCheckpointStage.CheckpointReadback, observation.Stage);
        Assert.Equal(fault == "ref" ? GitHubCheckpointReason.RefMismatch
            : GitHubCheckpointReason.Transport, observation.Reason);
        Assert.Equal(fault == "missing" ? GitHubFailureCode.NotFound
            : fault == "permission" ? GitHubFailureCode.Permission
            : fault == "exception" ? GitHubFailureCode.InvalidResponse
            : fault == "malformed" ? GitHubFailureCode.InvalidResponse : null, observation.TransportCode);
        Assert.Equal(fault == "missing" ? 404 : fault == "permission" ? 403 : fault == "malformed" ? 200 : null, observation.HttpStatus);
        Assert.Null(observation.Cancellation);
        handler.AfterResponse = (_, response) => Task.FromResult(response);
        // A later exact Observer read neither changes the failed ACK nor clears
        // the original cause. A separate store has its own observation lifetime.
        Assert.Equal(next.Artifact.Sha256, (await store.ReadAsync(default)).Sha256);
        Assert.Same(observation, store.CheckpointDiagnostic);
        using var otherClient = CheckpointClient(new CoordinationRemote());
        await using var other = GitHubCoordinationStore.Create(otherClient);
        Assert.Null(other.CheckpointDiagnostic);

        var usage = new InvocationUsage();
        usage.ObserveCheckpoint(observation);
        var terminal = GitHubProposalPresentation.Campaign(CliBuildIdentity.Current,
            new("execution", CampaignOperation.Resume, "campaign.state-publication-failure", before.CheckpointRevision));
        using var json = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(terminal, usage).StandardOutput);
        var reported = json.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure");
        Assert.DoesNotContain("private-error-body-key-sentinel", json.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(observation.Stage.ToString(), reported.GetProperty("stage").GetString());
        Assert.Equal(before.CheckpointRevision, json.RootElement.GetProperty("checkpointRevision").GetInt64());
        usage.ObserveCheckpoint(observation with { Stage = GitHubCheckpointStage.Gate });
        using var frozen = JsonDocument.Parse(GitHubProposalPresentation.WithUsage(terminal, usage).StandardOutput);
        Assert.Equal(reported.GetRawText(), frozen.RootElement.GetProperty("invocationUsage").GetProperty("checkpointFailure").GetRawText());
    }
}
