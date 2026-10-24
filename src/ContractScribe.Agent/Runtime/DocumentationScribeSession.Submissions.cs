using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;
using SolusAgent.Api.Candidates;

namespace ContractScribe.Agent.Runtime;

internal sealed partial class DocumentationScribeSession
{
    // Private wire history and Host receipts never enter product checkpoints or observations.
    private sealed class TerminalFrame(Guid physicalId, int epoch, string callId, ReadOnlyMemory<byte> bytes,
        DocumentationScribeAssistantContinuation? continuation)
    {
        internal Guid PhysicalId { get; } = physicalId;
        internal int Epoch { get; } = epoch;
        internal string CallId { get; } = callId;
        internal ReadOnlyMemory<byte> Bytes { get; } = bytes;
        internal DocumentationScribeAssistantContinuation? Continuation { get; set; } = continuation;
        internal ImmutableArray<byte>? Acknowledgement { get; set; }
    }
    private readonly IDocumentationScribeSubmissionHost? submissionHost;
    private ImmutableDictionary<Guid, TerminalFrame> terminalFrames = ImmutableDictionary<Guid, TerminalFrame>.Empty;
    private ImmutableDictionary<int, (string Hash, ImmutableArray<byte> Ack, bool Rejected)> submissionReceipts =
        ImmutableDictionary<int, (string, ImmutableArray<byte>, bool)>.Empty;
    private TerminalFrame? pendingTerminalFrame;

    private void RestoreProgress(DocumentationScribeRunResult? restored, DocumentationScribeRuntimeOptions options)
    {
        if (restored is null) return;
        _ = DocumentationScribeValidation.BindValidatedRunOutcome(State.Request, restored.AttemptId, restored);
        if (restored.RunEnvelope.ProviderConfigurationId != options.ProviderConfigurationId
            || restored.RunEnvelope.ModelConfigurationId != options.ModelConfigurationId
            || restored.RunEnvelope.ScribeProtocolId != options.ScribeProtocolId)
            throw new ArgumentException("Restored progress belongs to another configuration.", nameof(restored));
        State.Submissions = restored.Submissions.ToImmutableDictionary(item => item.TargetId, StringComparer.Ordinal);
        State.TargetFailures = restored.TargetFailures.ToImmutableDictionary(item => item.TargetId, StringComparer.Ordinal);
        State.RestoredClosedTargets = restored.Submissions.Select(item => item.TargetId)
            .Concat(restored.Stop is DocumentationScribeFinishedTerminal ? restored.TargetFailures.Select(item => item.TargetId) : [])
            .ToImmutableHashSet(StringComparer.Ordinal);
        State.RepairCount = restored.RepairCount;
        State.NextSequence = restored.NextSequence;
        foreach (var reference in restored.DynamicEvidenceReferences)
        {
            if (!State.TryChargeDynamicEvidence(reference))
                throw new ArgumentException("Restored progress exceeds evidence bounds.", nameof(restored));
        }
        if (State.IsEvidenceBudgetExceeded) throw new ArgumentException("Restored progress exceeds evidence bounds.", nameof(restored));
        // No old transcript, assistant continuation, provider usage or physical dispatch is restored.
    }

    private async Task<CandidateFeedback?> SubmitProductAsync(CandidateSubmission submission, CancellationToken token)
    {
        if (!await DrainAsync()) return new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Failed);
        var frame = pendingTerminalFrame ?? throw new InvalidOperationException("scribe.submission.missing");
        if (submission.Payload != Encoding.UTF8.GetString(frame.Bytes.Span))
            throw new InvalidOperationException("scribe.submission.mismatched");
        Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token, allowReturnedTerminal: true);
        if (Result is not null) return End(false);
        if (State.ToolCallCount >= State.Request.Limits.MaximumToolCalls)
        { Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget); return End(false); }
        var permit = await State.Scope.BeginHostAsync(DocumentationScribeHostOperation.TerminalSubmission, token);
        if (permit is null)
        {
            Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
            return End(false);
        }
        try
        {
            State.CallerCancellation.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (!permit.TryBeginOperation())
            {
                State.Scope.ObserveDeadline(permit);
                Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token,
                    State.Scope.ResourceBudgetReached ? DocumentationScribeFailureCode.Budget : DocumentationScribeFailureCode.Timeout);
                return End(false);
            }
            State.ToolCallCount++;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, permit.RemainingMilliseconds)), State.Scope.Allowance.Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, State.CallerCancellation, deadline.Token);
            permit.ObserveCancellation(deadline.Token, token, State.CallerCancellation, linked.Token);
            try
            {
                _ = new UTF8Encoding(false, true).GetString(frame.Bytes.Span);
                var normalized = CanonicalJson.Normalize(frame.Bytes);
                using var document = JsonDocument.Parse(normalized.AsMemory());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("scribeRequestSha256", out var hash) || hash.ValueKind != JsonValueKind.String
                    || hash.GetString() != State.Request.ArtifactSha256)
                    return Reject("scribe.submission.invalid-correlation");
                if (kind.GetString() == "finish")
                {
                    if (!Properties(root, "kind", "scribeRequestSha256")) return Reject("scribe.submission.invalid-shape");
                    Result = new DocumentationScribeTerminalReducer().CommitFinish(State, token);
                    return End(true);
                }
                if (kind.GetString() != "submit" || !Properties(root, "kind", "scribeRequestSha256", "sequence", "items")
                    || root.GetProperty("sequence").ValueKind != JsonValueKind.Number
                    || !root.GetProperty("sequence").TryGetInt32(out var sequence) || sequence < 1
                    || root.GetProperty("items").ValueKind != JsonValueKind.Array
                    || root.GetProperty("items").GetArrayLength() is < 1 or > DocumentationScribeValidation.MaximumSubmissionBatchSize)
                    return Reject("scribe.submission.invalid-shape");
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(normalized.AsSpan())).ToLowerInvariant();
                if (submissionReceipts.TryGetValue(sequence, out var prior))
                {
                    if (prior.Hash != digest) return Reject("scribe.submission.changed-replay");
                    frame.Acknowledgement = prior.Ack;
                    return Continue(prior.Rejected, prior.Ack);
                }
                if (sequence != State.NextSequence) return Reject("scribe.submission.out-of-order");
                var acknowledgements = new List<object>();
                var rejected = false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in root.GetProperty("items").EnumerateArray())
                {
                    if (linked.IsCancellationRequested) break;
                    string? targetId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("targetId", out var id)
                        && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                    if (targetId is null || !Properties(item, "targetId", "result")
                        || !State.Request.ExecutableTargetIds.Contains(targetId, StringComparer.Ordinal))
                    { Add(targetId is not null && State.Request.ExecutableTargetIds.Contains(targetId, StringComparer.Ordinal) ? targetId : null, "scribe.submission.foreign-target"); continue; }
                    if (!seen.Add(targetId)) { Add(targetId, "scribe.submission.duplicate-target"); continue; }
                    var parsed = DocumentationScribeValidation.ValidateTargetSubmission(State.Request, targetId,
                        State.ActiveDynamicEvidenceReferences.AddRange(State.Submissions.TryGetValue(targetId, out var priorSubmission)
                            ? priorSubmission.Validation.DynamicEvidenceReferences.Where(reference => !State.ActiveDynamicEvidenceReferences.Any(active => active.EvidenceReferenceId == reference.EvidenceReferenceId)) : []),
                        Encoding.UTF8.GetBytes(item.GetProperty("result").GetRawText()));
                    if (State.Submissions.TryGetValue(targetId, out var existing))
                    {
                        if (existing.Stage == DocumentationScribeAcknowledgementStage.Received)
                        { Add(targetId, "scribe.submission.host-unconfirmed"); Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token); break; }
                        if (parsed.Submission?.ContentSha256 == existing.ContentSha256)
                        { acknowledgements.Add(Ack(existing, "already-accepted")); continue; }
                        Add(targetId, "scribe.submission.target-closed"); continue;
                    }
                    if (State.RestoredClosedTargets.Contains(targetId)) { Add(targetId, "scribe.submission.target-closed"); continue; }
                    if (!State.AttemptedTargets.Contains(targetId))
                    {
                        if (State.AttemptedTargets.Count >= State.Scope.MaximumTargets)
                        { Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget); break; }
                        State.AttemptedTargets = State.AttemptedTargets.Add(targetId);
                    }
                    if (parsed.Submission is not { } validation)
                    {
                        var code = parsed.Failure?.Code ?? "scribe.result.invalid-terminal";
                        State.TargetFailures = State.TargetFailures.SetItem(targetId, new(targetId, code));
                        Add(targetId, code); continue;
                    }
                    if (!CanRetain(validation)) break;
                    // Retain received content before calling the Host: interruption cannot imply rollback.
                    var received = DocumentationScribeValidation.AcknowledgeSubmission(validation,
                        new(State.Request.ArtifactSha256, targetId, validation.ContentSha256, DocumentationScribeAcknowledgementStage.Received));
                    State.Submissions = State.Submissions.SetItem(targetId, received);
                    State.TargetFailures = State.TargetFailures.Remove(targetId);
                    DocumentationScribeSubmissionAcknowledgement? acknowledgement;
                    if (submissionHost is null)
                        acknowledgement = new(State.Request.ArtifactSha256, targetId,
                            validation.ContentSha256, DocumentationScribeAcknowledgementStage.Validated);
                    else
                    {
                        var pending = submissionHost.AcknowledgeAsync(validation, linked.Token).AsTask();
                        try { acknowledgement = await pending.WaitAsync(linked.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            // Stop observing under the original authority even if the Host ignores
                            // cancellation. Its late effects require readback, never replay or mutation
                            // of this received receipt after the product observation has settled.
                            _ = DocumentationScribeTaskObservation.ObserveLateAsync(pending);
                            throw;
                        }
                    }
                    if (acknowledgement is null)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("scribe.submission.host-unconfirmed");
                    }
                    // A correlated completed receipt remains a fact even if observation was just cancelled.
                    var accepted = DocumentationScribeValidation.AcknowledgeSubmission(validation, acknowledgement);
                    State.Submissions = State.Submissions.SetItem(targetId, accepted);
                    acknowledgements.Add(Ack(accepted, "acknowledged"));
                    if (accepted.Stage == DocumentationScribeAcknowledgementStage.Received)
                    { Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token); break; }
                    if (linked.IsCancellationRequested) break;
                }
                if (State.NextSequence == int.MaxValue)
                    Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
                else State.NextSequence++;
                if (rejected) State.RepairCount = Math.Min(DocumentationScribeValidation.MaximumInvalidSubmissions, State.RepairCount + 1);
                var ack = CanonicalJson.Serialize(new
                {
                    sequence,
                    items = acknowledgements,
                    nextSequence = State.NextSequence,
                    repairCount = State.RepairCount
                });
                frame.Acknowledgement = ack;
                submissionReceipts = submissionReceipts.Add(sequence, (digest, ack, rejected));
                if (ack.Length > CandidateFeedback.MaximumCorrectionBytes)
                    throw new InvalidOperationException("scribe.submission.ack-over-bound");
                if (rejected && State.RepairCount >= DocumentationScribeValidation.MaximumInvalidSubmissions)
                    Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.repair.exhausted");
                if (Result is null && State.AttemptedTargets.Count >= State.Scope.MaximumTargets
                    && State.AttemptedTargets.All(id => State.Submissions.ContainsKey(id))
                    && State.Request.ExecutableTargetIds.Any(id => !State.Submissions.ContainsKey(id) && !State.RestoredClosedTargets.Contains(id)))
                    Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Budget);
                Result ??= new DocumentationScribeTerminalReducer().TryCommitPriority(State, token);
                return Result is null ? Continue(rejected, ack) : End(false);

                void Add(string? targetId, string code)
                { rejected = true; acknowledgements.Add(new { targetId, disposition = "rejected", code }); }
            }
            catch (DecoderFallbackException)
            { Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.result.invalid-utf8"); return End(false); }
            catch (JsonException)
            { Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, "scribe.submission.invalid-shape"); return End(false); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token); return End(false); }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested && !State.CallerCancellation.IsCancellationRequested)
                    State.Scope.ObserveDeadline(permit);
                Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Timeout);
                return End(false);
            }
            finally
            {
                // A completed snapshot is reported within the existing TerminalSubmission permit.
                // No second permit, renewed deadline, transcript or Host capability enters the Agent.
                if (submissionHost is not null && !linked.IsCancellationRequested)
                {
                    var progress = DocumentationScribeValidation.CreateProgressNotification(State.Request, State.AttemptId,
                        State.RepairCount, State.NextSequence, State.AttemptedTargets, State.Submissions.Values,
                        State.TargetFailures.Values, Result is not null, finished: Result?.Stop is DocumentationScribeFinishedTerminal);
                    Task<DocumentationScribeProgressAcknowledgement?>? pending = null;
                    try
                    {
                        pending = submissionHost.ObserveProgressAsync(progress, linked.Token).AsTask();
                        var acknowledgement = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                        if (acknowledgement?.ProgressSha256 != progress.ProgressSha256)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (pending is not null) _ = DocumentationScribeTaskObservation.ObserveLateAsync(pending);
                        if (!token.IsCancellationRequested && !State.CallerCancellation.IsCancellationRequested)
                            State.Scope.ObserveDeadline(permit);
                        Result = new DocumentationScribeTerminalReducer().CommitFailure(State, token, DocumentationScribeFailureCode.Timeout);
                    }
                    catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                    {
                        Result = new DocumentationScribeTerminalReducer().CommitInternal(State, token);
                    }
                }
            }
        }
        finally
        {
            if (!await State.Scope.CompleteHostAsync(permit, CancellationToken.None))
                throw new InvalidOperationException("scribe.submission.settlement-unconfirmed");
        }

        CandidateFeedback End(bool accepted) => new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, accepted ? CandidateDecision.Accept : CandidateDecision.Reject, CandidateContinuation.End);
        CandidateFeedback Continue(bool rejected, ImmutableArray<byte> ack) => new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, rejected ? CandidateDecision.Reject : CandidateDecision.Accept,
            CandidateContinuation.Continue, Encoding.UTF8.GetString(ack.AsSpan()));
        CandidateFeedback Reject(string code)
        {
            State.RepairCount = Math.Min(DocumentationScribeValidation.MaximumInvalidSubmissions, State.RepairCount + 1);
            var ack = CanonicalJson.Serialize(new { disposition = "rejected", code, nextSequence = State.NextSequence, repairCount = State.RepairCount });
            frame.Acknowledgement = ack;
            if (State.RepairCount >= DocumentationScribeValidation.MaximumInvalidSubmissions)
            { Result = new DocumentationScribeTerminalReducer().CommitValidation(State, token, code); return End(false); }
            return Continue(true, ack);
        }
    }

    private bool CanRetain(DocumentationScribeValidatedSubmission validation)
    {
        // This is the longest encoded stage, including for proposal receipts.
        var largestStage = DocumentationScribeAcknowledgementStage.DurablyRecorded;
        var prospective = State.Submissions.SetItem(validation.TargetId, DocumentationScribeValidation.AcknowledgeSubmission(validation,
            new(State.Request.ArtifactSha256, validation.TargetId, validation.ContentSha256, largestStage)));
        var stop = DocumentationScribeValidation.CreateFailureResult(State.Request, State.AttemptId,
            DocumentationScribeFailureCode.Budget, State.CreateEnvelope([], State.ElapsedMilliseconds, allowObservedOverrun: true));
        var complete = DocumentationScribeValidation.WithProgress(State.Request, stop, prospective.Values.ToImmutableArray(),
            State.TargetFailures.Remove(validation.TargetId).Values.ToImmutableArray(), State.RepairCount, State.NextSequence);
        try
        {
            // Full existing coverage/progress is counted above. Three invalid batches can add
            // at most 24 bounded failure rows; 16KiB also covers scalar observations, one
            // closed diagnostic and final-stop growth before the next capacity check.
            if (DocumentationScribeRunResultWriter.Write(State.Request, complete).Length
                <= DocumentationScribeContract.MaximumArtifactUtf8Bytes - 16_384) return true;
        }
        catch (JsonException)
        {
            // An unrepresentable prospective result takes the controlled Budget stop below.
        }
        Result = new DocumentationScribeTerminalReducer().CommitFailure(State, CancellationToken.None, DocumentationScribeFailureCode.Budget);
        return false;
    }

    private static object Ack(DocumentationScribeTargetSubmission submission, string disposition) => new
    { submission.TargetId, submission.ContentSha256, stage = DocumentationScribeValidation.StageId(submission.Stage), disposition };

    private static bool Properties(JsonElement element, params string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Count() == names.Length && names.All(name => element.TryGetProperty(name, out _));
}
