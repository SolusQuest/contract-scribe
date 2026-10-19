using System.Collections.Immutable;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    internal async ValueTask<CampaignCheckpointReadKind> AcquireCheckpointAsync(CancellationToken token)
    {
        await checkpointGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (client.CheckpointAuthority is not { } authority || invocationOwner is not null)
                return CampaignCheckpointReadKind.Unsafe;
            var remote = await ReadRemoteCheckpointAsync(token).ConfigureAwait(false);
            var target = await client.GetRefAsync(authority.ReadAuthority.TargetRef, token).ConfigureAwait(false);
            if (target.Value?.Oid != authority.ExpectedBaseOid) return CampaignCheckpointReadKind.Unsafe;
            if (client.ServerTime is not { } now) return CampaignCheckpointReadKind.LeaseUnverifiable;
            if (remote?.Document.Claim is { } prior && now.ToUnixTimeSeconds() < prior.ExpiresUnixSeconds)
                return CampaignCheckpointReadKind.LeaseConflict;
            invocationOwner = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            invocationEpoch = checked((remote?.Document.Claim?.Epoch ?? 0) + 1);
            var document = remote?.Document.RetainAt(remote.Head) ?? new GitHubCoordinationDocument(
                GitHubCoordinationDocument.CurrentFormat, authority.ReadAuthority.RepositoryOwner + "/" + authority.ReadAuthority.RepositoryName,
                authority.ReadAuthority.TargetRef, authority.ExpectedBaseOid, authority.ReadAuthority.CampaignLineage,
                ZeroOid, null, null, null, null, null);
            document = document with
            {
                PredecessorOid = remote?.Head ?? ZeroOid,
                TargetCommitOid = authority.ExpectedBaseOid,
                Claim = new(invocationOwner, invocationEpoch, now.AddSeconds(ClaimSeconds).ToUnixTimeSeconds()),
            };
            if (!await WriteDocumentAsync(document, [], token).ConfigureAwait(false))
            {
                StopCheckpoint();
                return CampaignCheckpointReadKind.LeaseUnverifiable;
            }
            renewal = RenewCheckpointAsync();
            return CampaignCheckpointReadKind.Found;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            CheckpointFailure = exception is GitHubCoordinationException boundary ? boundary.TransportFailure
                : new(exception is OperationCanceledException ? GitHubFailureCode.Cancelled : GitHubFailureCode.HostFailure);
            StopCheckpoint(); return CampaignCheckpointReadKind.Unreadable;
        }
        finally { checkpointGate.Release(); }
    }

    private async ValueTask<CampaignCheckpointWriteResult> WriteCheckpointAsync(long? expectedRevision, string? expectedSha,
        ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token)
    {
        await checkpointGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var parsed = CampaignStateJson.Parse(bytes);
            if (!parsed.IsValid || parsed.Artifact is not { } artifact || artifact.Sha256 != sha
                || artifact.CheckpointRevision != revision || !bytes.Span.SequenceEqual(artifact.ExactUtf8Json.AsSpan()))
                return new(CampaignCheckpointWriteKind.Unsafe);
            var remote = await ReadRemoteCheckpointAsync(token).ConfigureAwait(false);
            if (remote is null || !Owns(remote.Document)) return new(CampaignCheckpointWriteKind.LeaseConflict);
            if (expectedRevision is null && remote.Artifact is not null) return new(CampaignCheckpointWriteKind.AlreadyPresent);
            if (expectedRevision is not null && remote.Artifact is null) return new(CampaignCheckpointWriteKind.PredecessorMissing);
            if (remote.Artifact is { } current && (current.CheckpointRevision != expectedRevision || current.Sha256 != expectedSha))
                return new(CampaignCheckpointWriteKind.CurrentMismatch);
            var document = remote.Document.RetainAt(remote.Head) with { PredecessorOid = remote.Head };
            var blobs = new List<GitHubBlob>();
            var checkpoint = Blob(bytes.Span);
            blobs.Add(checkpoint);
            document = document with { Checkpoint = new(checkpoint.Oid, sha, revision) };
            if (boundPlan is { } plan && plan.ExecutionCommitmentSha256 == artifact.State.Snapshot.ExecutionCommitmentSha256
                && plan.BatchIdentity == artifact.State.Batch.Identity)
            {
                var planBlob = Blob(plan.ExactUtf8Json.AsSpan());
                blobs.Add(planBlob);
                document = document with { Plan = new(planBlob.Oid, plan.Sha256, plan.ExecutionCommitmentSha256, plan.BatchIdentity) };
            }
            if (document.Plan is not { } selected || selected.ExecutionCommitment != artifact.State.Snapshot.ExecutionCommitmentSha256
                || selected.BatchIdentity != artifact.State.Batch.Identity || artifact.State.CampaignLineage != document.CampaignLineage)
                return new(CampaignCheckpointWriteKind.Unsafe);
            if (artifact.State.ArchivedDispatchPrefix != remote.Artifact?.State.ArchivedDispatchPrefix)
            {
                if (remote.Artifact is not { } before) return new(CampaignCheckpointWriteKind.Unsafe);
                var evidence = CampaignDispatchArchive.VerifyTransition(before, artifact);
                var witness = Blob(before.ExactUtf8Json.AsSpan());
                blobs.Add(witness);
                document = document with { Archive = new(witness.Oid, evidence.WitnessSha256, GitHubCoordinationDocument.CurrentCommit) };
            }
            if (!await WriteDocumentAsync(document, blobs, token).ConfigureAwait(false))
            { StopCheckpoint(); return new(CampaignCheckpointWriteKind.PublicationFailure); }
            return new(CampaignCheckpointWriteKind.Written);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { StopCheckpoint(); return new(CampaignCheckpointWriteKind.PublicationFailure); }
        finally { checkpointGate.Release(); }
    }

    private async ValueTask<bool> WriteDocumentAsync(GitHubCoordinationDocument document, IEnumerable<GitHubBlob> blobs,
        CancellationToken token, bool requireLiveClaim = true)
    {
        var prepared = GitHubCoordinationObjects.Prepare(document);
        var payloads = blobs.ToImmutableArray();
        client.BindCheckpointWrite(new(prepared, payloads));
        try
        {
            var created = await CreateObjectsAsync(prepared, token, payloads).ConfigureAwait(false);
            if (created is not null || requireLiveClaim && !Owns(document)) return false;
            var result = await UpdateCoordinationRefAsync(new(coordinationRef, document.PredecessorOid,
                prepared.CommitOid, document.PredecessorOid == ZeroOid), token).ConfigureAwait(false);
            if (result.Delivery == GitHubDelivery.NotDispatched) return false;
            using var recovery = new CancellationTokenSource(RecoveryTimeout);
            var current = await client.GetRefAsync(coordinationRef, recovery.Token).ConfigureAwait(false);
            if (current.Value?.Oid != prepared.CommitOid) return false;
            var read = await ReadDocumentAsync(prepared.CommitOid, recovery.Token).ConfigureAwait(false);
            return read == document && (!requireLiveClaim || Owns(read));
        }
        finally { client.BindCheckpointWrite(null); }
    }

    private static GitHubBlob Blob(ReadOnlySpan<byte> bytes) => new(
        GitHubCoordinationObjects.ObjectOid("blob", bytes), ImmutableArray.CreateRange(bytes.ToArray()));

    private async Task RenewCheckpointAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), checkpointLifetime.Token).ConfigureAwait(false);
                await checkpointGate.WaitAsync(checkpointLifetime.Token).ConfigureAwait(false);
                try
                {
                    var current = await ReadRemoteCheckpointAsync(checkpointLifetime.Token).ConfigureAwait(false);
                    if (current is null || !Owns(current.Document)) { StopCheckpoint(); return; }
                    var document = current.Document.RetainAt(current.Head) with
                    {
                        PredecessorOid = current.Head,
                        Claim = current.Document.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.AddSeconds(ClaimSeconds).ToUnixTimeSeconds() },
                    };
                    if (!await WriteDocumentAsync(document, [], checkpointLifetime.Token).ConfigureAwait(false))
                    { StopCheckpoint(); return; }
                }
                finally { checkpointGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (checkpointLifetime.IsCancellationRequested) { }
        catch { StopCheckpoint(); }
    }

    private void StopCheckpoint()
    {
        checkpointStopped = true;
        checkpointLifetime.Cancel();
    }

    internal async Task<T> WithPublicationAsync<T>(Func<Task<T>> publish, CancellationToken token)
    {
        await checkpointGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await ReadRemoteCheckpointAsync(token).ConfigureAwait(false);
            if (current is null || !Owns(current.Document) || current.Artifact is null) throw new GitHubCoordinationException();
            var renewed = current.Document.RetainAt(current.Head) with
            {
                PredecessorOid = current.Head,
                Claim = current.Document.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.AddSeconds(ClaimSeconds).ToUnixTimeSeconds() },
            };
            if (!await WriteDocumentAsync(renewed, [], token).ConfigureAwait(false)) throw new GitHubCoordinationException();
            return await publish().ConfigureAwait(false);
        }
        finally { checkpointGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        checkpointLifetime.Cancel();
        if (renewal is not null) await renewal.ConfigureAwait(false);
        await checkpointGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!checkpointStopped && invocationOwner is not null)
            {
                using var deadline = new CancellationTokenSource(RecoveryTimeout);
                var current = await ReadRemoteCheckpointAsync(deadline.Token).ConfigureAwait(false);
                if (current is not null && Owns(current.Document))
                    await WriteDocumentAsync(current.Document.RetainAt(current.Head) with
                    {
                        PredecessorOid = current.Head,
                        Claim = current.Document.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.ToUnixTimeSeconds() },
                    }, [], deadline.Token, requireLiveClaim: false).ConfigureAwait(false);
            }
        }
        catch { /* Expiry retains all remote facts when release cannot be established. */ }
        finally { checkpointStopped = true; checkpointGate.Release(); checkpointLifetime.Dispose(); }
    }

    private ValueTask<GitHubApiResult<GitHubAcknowledgement>> UpdateCoordinationRefAsync(GitHubUpdateRef request,
        CancellationToken token) => client.UpdateRefAsync(request, token);
}
