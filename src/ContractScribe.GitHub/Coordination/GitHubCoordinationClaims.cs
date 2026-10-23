using System.Collections.Immutable;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed partial class GitHubCoordinationStore
{
    internal async ValueTask<CampaignCheckpointReadKind> AcquireCheckpointAsync(CancellationToken token)
    {
        var attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Acquire);
        try { await checkpointGate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.Gate, exception, token); throw; }
        try
        {
            if (client.CheckpointAuthority is not { } authority || invocationOwner is not null)
                return CampaignCheckpointReadKind.Unsafe;
            var remote = await ReadRemoteCheckpointAsync(token, attempt: attempt).ConfigureAwait(false);
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
            if (await WriteDocumentAsync(document, [], token, attempt: attempt).ConfigureAwait(false) is not null)
            {
                StopCheckpoint();
                return CampaignCheckpointReadKind.LeaseUnverifiable;
            }
            renewal = RenewCheckpointAsync();
            return CampaignCheckpointReadKind.Found;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, token);
            CheckpointFailure = exception is GitHubCoordinationException boundary ? boundary.TransportFailure
                : new(exception is OperationCanceledException ? GitHubFailureCode.Cancelled : GitHubFailureCode.HostFailure);
            StopCheckpoint();
            if (CheckpointFailure?.Code == GitHubFailureCode.Conflict) return CampaignCheckpointReadKind.LeaseConflict;
            return exception is GitHubCoordinationException { TransportFailure: null } or CampaignStateValidationException
                ? CampaignCheckpointReadKind.Invalid : CampaignCheckpointReadKind.Unreadable;
        }
        finally { checkpointGate.Release(); }
    }

    private async ValueTask<CampaignCheckpointWriteResult> WriteCheckpointAsync(long? expectedRevision, string? expectedSha,
        ReadOnlyMemory<byte> bytes, long revision, string sha, CancellationToken token)
    {
        var attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Write, revision);
        var stage = GitHubCheckpointStage.Gate;
        try { await checkpointGate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        { ObserveCheckpointException(attempt, stage, exception, token); throw; }
        try
        {
            using var semanticRecovery = client.EnterSemanticRecovery(semanticWindow);
            var parsed = CampaignStateJson.Parse(bytes);
            if (!parsed.IsValid || parsed.Artifact is not { } artifact || artifact.Sha256 != sha
                || artifact.CheckpointRevision != revision || !bytes.Span.SequenceEqual(artifact.ExactUtf8Json.AsSpan()))
                return new(CampaignCheckpointWriteKind.Unsafe);
            var proof = new ReadProof();
            stage = GitHubCheckpointStage.PredecessorRead;
            var remote = await ReadCallerCheckpointAsync(token, proof, attempt: attempt).ConfigureAwait(false);
            if (remote is null || !Owns(remote.Document))
            {
                ObserveCheckpointFailure(attempt, GitHubCheckpointStage.OwnerConfirmation, GitHubCheckpointReason.OwnerMismatch);
                return new(CampaignCheckpointWriteKind.LeaseConflict);
            }
            if (expectedRevision is null && remote.Artifact is not null) return new(CampaignCheckpointWriteKind.AlreadyPresent);
            if (expectedRevision is not null && remote.Artifact is null) return new(CampaignCheckpointWriteKind.PredecessorMissing);
            if (remote.Artifact is { } current && (current.CheckpointRevision != expectedRevision || current.Sha256 != expectedSha))
                return new(CampaignCheckpointWriteKind.CurrentMismatch);
            if (!await PublicationAllowsCheckpointAsync(remote, artifact, token).ConfigureAwait(false))
                return new(CampaignCheckpointWriteKind.Unsafe);
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
            stage = GitHubCheckpointStage.CreateObjects;
            if (await WriteDocumentAsync(document, blobs, token, proof: proof, attempt: attempt).ConfigureAwait(false) is not null)
            { StopCheckpoint(); return new(CampaignCheckpointWriteKind.PublicationFailure); }
            stage = GitHubCheckpointStage.CheckpointReadback;
            var observed = await ReadCallerCheckpointAsync(token, proof, verifyTarget: true, attempt: attempt, stage: stage).ConfigureAwait(false);
            GitHubCheckpointReason? reason = observed?.Artifact is not { } ? GitHubCheckpointReason.CheckpointMismatch
                : !Owns(observed.Document) ? GitHubCheckpointReason.OwnerMismatch
                : observed.Document != document ? GitHubCheckpointReason.DocumentMismatch
                : observed.Artifact.CheckpointRevision != revision || observed.Artifact.Sha256 != sha
                    || !observed.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(bytes.Span) ? GitHubCheckpointReason.CheckpointMismatch
                : !ConfirmCheckpointLease(observed.Document, attempt) ? GitHubCheckpointReason.LeaseUnverifiable : null;
            if (reason is { } failed)
            {
                ObserveCheckpointFailure(attempt, stage, failed);
                StopCheckpoint(); return new(CampaignCheckpointWriteKind.PublicationFailure);
            }
            var readback = observed!.Artifact!;
            var actual = CampaignCheckpointReadResult.Found(
                readback.ExactUtf8Json.AsSpan(), readback.CheckpointRevision, readback.Sha256, observed.ArchiveEvidence);
            ObserveSemanticReadback(observed, actual);
            return CampaignCheckpointWriteResult.Written(actual);
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token)
        { ObserveCheckpointException(attempt, stage, exception, token); throw; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { ObserveCheckpointException(attempt, stage, exception, token); StopCheckpoint(); return new(CampaignCheckpointWriteKind.PublicationFailure); }
        finally { checkpointGate.Release(); }
    }

    // A null failure means the same exact write/readback/lease proof succeeded.
    // Keep execution failures separate from the diagnostic observer.
    private GitHubCoordinationResult RetainCheckpointFailure(GitHubCoordinationResult result)
    {
        CheckpointFailure ??= result.Failure?.ReadbackFailure ?? result.Failure?.TransportFailure;
        return result;
    }

    private static void RethrowCheckpointCancellation(GitHubCoordinationResult result, CancellationToken token)
    {
        if (token.IsCancellationRequested && result.Failure is
            {
                Delivery: GitHubDelivery.NotDispatched, ReadbackFailure: null,
                TransportFailure.Code: GitHubFailureCode.Cancelled or GitHubFailureCode.Timeout
            } failure)
            throw new OperationCanceledException("The checkpoint operation was cancelled.",
                new GitHubCoordinationException(failure: failure), token);
    }

    private GitHubCoordinationResult CheckpointExceptionFailure(Exception exception)
    {
        if (exception.InnerException is GitHubCoordinationException { Failure: { } original })
            return new(GitHubCoordinationOutcome.Failed, failure: original);
        if (exception is GitHubCoordinationException { Failure: { } failure })
            return new(GitHubCoordinationOutcome.Failed, failure: failure);
        return exception is OperationCanceledException
            ? DomainFailure(GitHubCoordinationFailureKind.Transport, client.CancellationFailure)
            : exception is GitHubCoordinationException or CampaignStateValidationException
                ? DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch)
                : DomainFailure(GitHubCoordinationFailureKind.Transport, new(GitHubFailureCode.HostFailure));
    }

    private async ValueTask<GitHubCoordinationResult?> WriteDocumentAsync(GitHubCoordinationDocument document, IEnumerable<GitHubBlob> blobs,
        CancellationToken token, bool requireLiveClaim = true, GitHubCoordinationDocument? livePredecessor = null,
        GitHubPublicationEvent? publication = null, ReadProof? proof = null, CheckpointAttempt? attempt = null)
    {
        var prepared = GitHubCoordinationObjects.Prepare(document, publication);
        var payloads = blobs.Concat(publication is null ? [] : new[] { Blob(publication.Encode()) }).ToImmutableArray();
        client.BindCheckpointWrite(new(prepared, payloads));
        var stage = GitHubCheckpointStage.CreateObjects;
        try
        {
            var created = await CreateObjectsAsync(prepared, token, payloads, proof).ConfigureAwait(false);
            if (created is not null)
            {
                var failure = created.Failure?.ReadbackFailure ?? created.Failure?.TransportFailure;
                ObserveCheckpointFailure(attempt, stage, failure?.Code == GitHubFailureCode.Cancelled
                    ? GitHubCheckpointReason.Cancelled : failure is null ? GitHubCheckpointReason.InvalidData : GitHubCheckpointReason.Transport,
                    failure, failure?.Code == GitHubFailureCode.Cancelled
                        ? token.IsCancellationRequested ? GitHubCheckpointCancellation.OperationToken : GitHubCheckpointCancellation.Unknown : null);
                RetainCheckpointFailure(created);
                RethrowCheckpointCancellation(created, token);
                return created;
            }
            if (requireLiveClaim && !Owns(document) || livePredecessor is not null && !Owns(livePredecessor))
            {
                ObserveCheckpointFailure(attempt, GitHubCheckpointStage.OwnerConfirmation, GitHubCheckpointReason.OwnerMismatch);
                return RetainCheckpointFailure(DomainFailure(GitHubCoordinationFailureKind.Conflict));
            }
            stage = GitHubCheckpointStage.RefMutation;
            var result = await UpdateCoordinationRefAsync(new(coordinationRef, document.PredecessorOid,
                prepared.CommitOid, document.PredecessorOid == ZeroOid), token).ConfigureAwait(false);
            if (result.Delivery == GitHubDelivery.NotDispatched)
            {
                ObserveCheckpointFailure(attempt, stage, result.Failure?.Code == GitHubFailureCode.Cancelled
                    ? GitHubCheckpointReason.Cancelled : result.Failure is null ? GitHubCheckpointReason.Unknown : GitHubCheckpointReason.Transport,
                    result.Failure, result.Failure?.Code == GitHubFailureCode.Cancelled
                        ? token.IsCancellationRequested ? GitHubCheckpointCancellation.OperationToken : GitHubCheckpointCancellation.Unknown : null);
                var failed = RetainCheckpointFailure(Failed(result));
                RethrowCheckpointCancellation(failed, token);
                return failed;
            }
            using var recovery = client.CreateRecoveryScope(RecoveryTimeout);
            stage = GitHubCheckpointStage.FinalRef;
            try
            {
                var finalRef = await client.GetRefAsync(coordinationRef, recovery.Token).ConfigureAwait(false);
                var current = GitHubObservedProposalIntegrity.Read(finalRef);
                if (current?.Oid != prepared.CommitOid)
                {
                    ObserveCheckpointFailure(attempt, stage, GitHubCheckpointReason.RefMismatch, finalRef.Failure);
                    return RetainCheckpointFailure(DomainFailure(finalRef.Failure is null
                        ? GitHubCoordinationFailureKind.Conflict : GitHubCoordinationFailureKind.Transport,
                        result.Failure, result.Delivery, result.Context, result.RequiredPermissions,
                        RecoveryFailure(finalRef.Failure, recovery)));
                }
                stage = GitHubCheckpointStage.CanonicalDocument;
                var read = await ReadDocumentAsync(prepared.CommitOid, recovery.Token, proof).ConfigureAwait(false);
                if (read != document)
                {
                    ObserveCheckpointFailure(attempt, stage, GitHubCheckpointReason.DocumentMismatch);
                    return RetainCheckpointFailure(DomainFailure(GitHubCoordinationFailureKind.ObjectMismatch,
                        result.Failure, result.Delivery, result.Context, result.RequiredPermissions));
                }
                stage = GitHubCheckpointStage.LeaseConfirmation;
                if (requireLiveClaim && !ConfirmCheckpointLease(read, attempt))
                {
                    ObserveCheckpointFailure(attempt, stage, GitHubCheckpointReason.LeaseUnverifiable);
                    return RetainCheckpointFailure(DomainFailure(GitHubCoordinationFailureKind.Conflict,
                        result.Failure, result.Delivery, result.Context, result.RequiredPermissions));
                }
                return null;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                ObserveCheckpointException(attempt, stage, exception, recovery.Token, recovery: true);
                var failure = CheckpointExceptionFailure(exception).Failure!;
                return RetainCheckpointFailure(DomainFailure(failure.Kind, result.Failure, result.Delivery,
                    result.Context, result.RequiredPermissions, RecoveryFailure(failure.ReadbackFailure ?? failure.TransportFailure, recovery)));
            }
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            ObserveCheckpointException(attempt, stage, exception, token);
            throw; // Preserve the public checkpoint caller's original cancellation boundary.
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            ObserveCheckpointException(attempt, stage, exception, token);
            return RetainCheckpointFailure(CheckpointExceptionFailure(exception));
        }
        finally { client.BindCheckpointWrite(null); }
    }

    private static GitHubBlob Blob(ReadOnlySpan<byte> bytes) => new(
        GitHubCoordinationObjects.ObjectOid("blob", bytes), ImmutableArray.CreateRange(bytes.ToArray()));

    private async Task RenewCheckpointAsync()
    {
        CheckpointAttempt? attempt = null;
        var stage = GitHubCheckpointStage.PredecessorRead;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), checkpointLifetime.Token).ConfigureAwait(false);
                await checkpointGate.WaitAsync(checkpointLifetime.Token).ConfigureAwait(false);
                try
                {
                    attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Renew);
                    stage = GitHubCheckpointStage.PredecessorRead;
                    var current = await ReadRemoteCheckpointAsync(checkpointLifetime.Token, attempt: attempt).ConfigureAwait(false);
                    if (current is null || !Owns(current.Document))
                    {
                        ObserveCheckpointFailure(attempt, GitHubCheckpointStage.OwnerConfirmation, GitHubCheckpointReason.OwnerMismatch);
                        StopCheckpoint(); return;
                    }
                    var document = current.Document.RetainAt(current.Head) with
                    {
                        PredecessorOid = current.Head,
                        Claim = current.Document.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.AddSeconds(ClaimSeconds).ToUnixTimeSeconds() },
                    };
                    stage = GitHubCheckpointStage.CreateObjects;
                    if (await WriteDocumentAsync(document, [], checkpointLifetime.Token, livePredecessor: current.Document,
                        attempt: attempt).ConfigureAwait(false) is not null)
                    { StopCheckpoint(); return; }
                }
                finally { checkpointGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (checkpointLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        { ObserveCheckpointException(attempt, stage, exception, checkpointLifetime.Token); StopCheckpoint(); }
    }

    private bool ConfirmCheckpointLease(GitHubCoordinationDocument document, CheckpointAttempt? attempt = null)
    {
        if (!Owns(document) || checkpointExpiry.IsCancellationRequested)
        {
            ObserveCheckpointFailure(attempt, GitHubCheckpointStage.LeaseConfirmation, GitHubCheckpointReason.LeaseUnverifiable);
            return false;
        }
        // Authenticated server time advances monotonically in the client. The
        // independent timer still fences execution if renewal waits on the gate
        // or transport; only an actually confirmed owned claim can rearm it.
        var remaining = DateTimeOffset.FromUnixTimeSeconds(document.Claim!.ExpiresUnixSeconds)
            - client.ServerTime!.Value - TimeSpan.FromSeconds(1);
        if (remaining <= TimeSpan.Zero)
        {
            ObserveCheckpointFailure(attempt, GitHubCheckpointStage.LeaseConfirmation, GitHubCheckpointReason.LeaseUnverifiable);
            StopCheckpoint(); return false;
        }
        checkpointExpiry.CancelAfter(remaining);
        return !checkpointStopped;
    }

    private void StopCheckpoint()
    {
        checkpointStopped = true;
        checkpointLifetime.Cancel();
    }

    internal async Task<T> WithPublicationAsync<T>(Func<Task<T>> publish, CancellationToken token)
    {
        var attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Renew);
        await checkpointGate.WaitAsync(token).ConfigureAwait(false);
        var renewalComplete = false;
        try
        {
            var current = await ReadRemoteCheckpointAsync(token, attempt: attempt).ConfigureAwait(false);
            if (current is null || !Owns(current.Document) || current.Artifact is null) throw new GitHubCoordinationException();
            var renewed = current.Document.RetainAt(current.Head) with
            {
                PredecessorOid = current.Head,
                Claim = current.Document.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.AddSeconds(ClaimSeconds).ToUnixTimeSeconds() },
            };
            if (await WriteDocumentAsync(renewed, [], token, livePredecessor: current.Document, attempt: attempt).ConfigureAwait(false) is { } failed)
                throw new GitHubCoordinationException(failure: failed.Failure);
            publicationClaimExpires = renewed.Claim!.ExpiresUnixSeconds;
            publicationHead = GitHubCoordinationObjects.Prepare(renewed).CommitOid;
            publicationRenewalFailure = null;
            publicationActive = true;
            renewalComplete = true;
            return await publish().ConfigureAwait(false);
        }
        catch (Exception exception) when (!renewalComplete && exception is not (OutOfMemoryException or StackOverflowException))
        { ObserveCheckpointException(attempt, GitHubCheckpointStage.PredecessorRead, exception, token); throw; }
        finally { publicationActive = false; checkpointGate.Release(); }
    }

    private bool publicationActive;
    private string? publicationHead;
    private long publicationClaimExpires;
    private readonly Dictionary<string, string> publicationRenewals = new(StringComparer.Ordinal);

    // Only this lifetime's read-back-confirmed claim-only CAS may carry a
    // publication capability forward. External movement never enters this map.
    internal string ResolvePublicationHead(string head)
    {
        while (publicationRenewals.TryGetValue(head, out var next)) head = next;
        return head;
    }

    // The publication scope owns checkpointGate. Service renewal at operation
    // boundaries without racing a prepared publication or paired ref CAS.
    private GitHubCoordinationResult? publicationRenewalFailure;

    private async ValueTask<GitHubCoordinationResult?> RenewPublicationClaimAsync(CancellationToken token)
    {
        if (!publicationActive) return null;
        if (publicationRenewalFailure is not null) return publicationRenewalFailure;
        token.ThrowIfCancellationRequested();
        if (checkpointStopped) return DomainFailure(GitHubCoordinationFailureKind.Conflict);
        if (client.ServerTime is { } now && now.ToUnixTimeSeconds() < publicationClaimExpires - (ClaimSeconds - 60)) return null;
        var attempt = BeginCheckpointOperation(GitHubCheckpointOperation.Renew);
        var stage = GitHubCheckpointStage.PredecessorRead;
        GitHubCoordinationResult Stop(GitHubCoordinationResult failure)
        {
            // Freeze this publication operation's actual failure before StopCheckpoint
            // can propagate owner cancellation through the execution token.
            publicationRenewalFailure ??= failure;
            StopCheckpoint();
            return publicationRenewalFailure;
        }
        try
        {
            var head = RequireRemote(await client.GetRefAsync(coordinationRef, token).ConfigureAwait(false)).Oid;
            if (head != publicationHead)
            {
                ObserveCheckpointFailure(attempt, GitHubCheckpointStage.FinalRef, GitHubCheckpointReason.RefMismatch);
                return Stop(DomainFailure(GitHubCoordinationFailureKind.Conflict));
            }
            var current = await ReadAcceptedDocumentAsync(head, token).ConfigureAwait(false);
            if (!Owns(current))
            {
                ObserveCheckpointFailure(attempt, GitHubCheckpointStage.OwnerConfirmation, GitHubCheckpointReason.OwnerMismatch);
                return Stop(DomainFailure(GitHubCoordinationFailureKind.Conflict));
            }
            var renewed = current.RetainAt(head) with
            {
                PredecessorOid = head,
                Claim = current.Claim! with { ExpiresUnixSeconds = client.ServerTime!.Value.AddSeconds(ClaimSeconds).ToUnixTimeSeconds() },
            };
            stage = GitHubCheckpointStage.CreateObjects;
            if (await WriteDocumentAsync(renewed, [], token, livePredecessor: current, attempt: attempt).ConfigureAwait(false) is { } failed)
                return Stop(failed);
            var renewedHead = GitHubCoordinationObjects.Prepare(renewed).CommitOid;
            publicationRenewals[head] = renewedHead;
            publicationHead = renewedHead;
            publicationClaimExpires = renewed.Claim!.ExpiresUnixSeconds;
            return null;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            ObserveCheckpointException(attempt, stage, exception, token);
            return Stop(CheckpointExceptionFailure(exception));
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Let an in-flight renewal finish before stopping its loop. Cancelling
        // its transport read/write would fence this owner and skip normal release.
        await checkpointGate.WaitAsync().ConfigureAwait(false);
        try
        {
            checkpointLifetime.Cancel();
            if (renewal is not null) await renewal.ConfigureAwait(false);
            if (!checkpointStopped && invocationOwner is not null)
            {
                using var deadline = client.CreateRecoveryScope(RecoveryTimeout);
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
        finally
        {
            checkpointStopped = true;
            checkpointExpiryRegistration.Dispose();
            checkpointExpiry.Dispose();
            checkpointGate.Release();
            checkpointLifetime.Dispose();
        }
    }

    private ValueTask<GitHubApiResult<GitHubAcknowledgement>> UpdateCoordinationRefAsync(GitHubUpdateRef request,
        CancellationToken token) => client.UpdateRefAsync(request, token);
}
