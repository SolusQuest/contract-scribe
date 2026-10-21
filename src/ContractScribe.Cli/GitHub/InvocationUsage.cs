using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Cli;

/// <summary>One command's source-free observations, never an admission or checkpoint authority.</summary>
internal sealed class InvocationUsage(TimeProvider? clock = null, Guid? invocationId = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly long startedAt = (clock ?? TimeProvider.System).GetTimestamp();
    private readonly Guid id = invocationId ?? Guid.NewGuid();
    private CampaignCheckpointState? state;
    private Dictionary<string, CampaignWorkItemState>? entry;
    private Dictionary<string, CampaignTargetProgressKind>? entryProgress;
    private Dictionary<string, string?> entryContent = [];
    private HashSet<string> entryFailures = [];
    private readonly HashSet<string> started = [], resumed = [], accepted = [], skipped = [], failed = [], reconstructed = [];
    private DocumentationScribeInvocationAllowance? allowance;
    private DocumentationScribeFinishReason? finish;
    private bool workUnavailable;

    internal static JsonElement Unavailable { get; } = JsonSerializer.SerializeToElement(new
    {
        invocationUsageVersion = 1,
        invocationId = (string?)null,
        campaignLineage = (string?)null,
        snapshotId = (string?)null,
        batchId = (string?)null,
        checkpointRevision = (long?)null,
        publicationOperationId = (string?)null,
        generationId = (string?)null,
        pullRequestUrl = (string?)null,
        work = (object?)null,
        calls = (object?)null,
        tokens = (object?)null,
        accounting = (object?)null,
        history = (object?)null,
        timing = (object?)null,
        termination = (object?)null,
        completeness = "unknown",
    }, JsonOptions);

    internal void Bind(DocumentationScribeInvocationAllowance value) => allowance = value;

    internal void ObserveState(CampaignCheckpointArtifact? artifact)
    {
        if (artifact is null || workUnavailable) return;
        try
        {
            var next = artifact.State;
            var byKey = next.WorkItems.ToDictionary(work => work.WorkItemKey, StringComparer.Ordinal);
            if (entry is null || state?.Snapshot.ExecutionCommitmentSha256 != next.Snapshot.ExecutionCommitmentSha256)
            {
                entry = byKey;
                entryProgress = next.TargetProgress.ToDictionary(progress => progress.Target.TargetKey, progress => progress.Kind, StringComparer.Ordinal);
                entryContent = next.ScribeProgress?.Members.ToDictionary(member => member.WorkItemKey, member => member.ContentSha256, StringComparer.Ordinal) ?? [];
                entryFailures = next.ScribeProgress?.TargetFailures.Select(failure => failure.TargetId).ToHashSet(StringComparer.Ordinal) ?? [];
                started.Clear(); resumed.Clear(); accepted.Clear(); skipped.Clear(); failed.Clear(); reconstructed.Clear();
            }
            var members = next.ScribeProgress?.Members.ToDictionary(member => member.WorkItemKey, StringComparer.Ordinal) ?? [];
            var newFailures = next.ScribeProgress?.TargetFailures.Where(failure => !entryFailures.Contains(failure.TargetId))
                .Select(failure => failure.TargetId).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var progress in next.TargetProgress)
            {
                var key = progress.Target.TargetKey;
                var work = byKey[progress.Target.WorkItemKey];
                var before = entry[work.WorkItemKey];
                if (before.OuterAttemptCount == 0 && work.OuterAttemptCount > 0) started.Add(key);
                if (UnresolvedStarted(before) && (work.OuterAttemptCount > before.OuterAttemptCount
                    || members.TryGetValue(work.WorkItemKey, out var member)
                        && (entryContent.GetValueOrDefault(work.WorkItemKey) is null && member.ContentSha256 is not null
                            || newFailures.Contains(member.TargetId)))) resumed.Add(key);
                if (before.Status != CampaignWorkStatus.Accepted && work.Status == CampaignWorkStatus.Accepted) accepted.Add(key);
                if (entryProgress![key] != progress.Kind)
                {
                    if (progress.Kind == CampaignTargetProgressKind.Skipped) skipped.Add(key);
                    if (progress.Kind is CampaignTargetProgressKind.Failed or CampaignTargetProgressKind.InfrastructureBlocked
                        or CampaignTargetProgressKind.Suppressed) failed.Add(key);
                }
            }
            state = next;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { workUnavailable = true; }
    }

    internal void ObserveExecution(CampaignCheckpointArtifact artifact, string workItemKey)
    {
        ObserveState(artifact);
        if (workUnavailable || entry?.GetValueOrDefault(workItemKey) is not { } before || !UnresolvedStarted(before)) return;
        foreach (var target in artifact.State.Batch.CompleteTargets.Where(target => target.WorkItemKey == workItemKey))
            resumed.Add(target.TargetKey);
    }

    internal void ObserveFinish(DocumentationScribeTerminal? terminal)
    {
        if (terminal is DocumentationScribeFinishedTerminal finished
            && (finish is null || finished.Reason == DocumentationScribeFinishReason.Context)) finish = finished.Reason;
    }

    internal void ObserveReconstruction(CampaignCheckpointArtifact? artifact)
    {
        ObserveState(artifact);
        if (artifact is null || entry is null || workUnavailable) return;
        var byKey = artifact.State.WorkItems.ToDictionary(work => work.WorkItemKey, StringComparer.Ordinal);
        foreach (var target in artifact.State.Batch.CompleteTargets.Where(target =>
            entry.GetValueOrDefault(target.WorkItemKey)?.Status == CampaignWorkStatus.Accepted
            && byKey[target.WorkItemKey].Status == CampaignWorkStatus.Accepted))
            reconstructed.Add(target.TargetKey);
    }

    private static bool UnresolvedStarted(CampaignWorkItemState work) => work.OuterAttemptCount > 0
        && work.Status is not (CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted);

    internal JsonElement Snapshot(GitHubProposalEnvelope envelope)
    {
        try { return CreateSnapshot(envelope); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Unavailable; }
    }

    private JsonElement CreateSnapshot(GitHubProposalEnvelope envelope)
    {
        var observation = allowance?.Observe();
        var zero = new DocumentationScribeTokenObservation(0, 0);
        var tokens = new
        {
            input = observation?.Input ?? zero,
            cachedInput = observation?.CachedInput ?? zero,
            uncachedInput = observation?.UncachedInput ?? zero,
            output = observation?.Output ?? zero,
            reasoning = observation?.Reasoning ?? zero,
        };
        var missing = new[] { tokens.input, tokens.cachedInput, tokens.uncachedInput, tokens.output, tokens.reasoning }
            .Any(value => value.MissingDispatches > 0);
        var selected = state?.Batch.SelectedTargetKeys.ToHashSet(StringComparer.Ordinal);
        var progress = state?.TargetProgress.Where(item => selected!.Contains(item.Target.TargetKey)).ToArray();
        var scope = workUnavailable ? null : state;
        var stop = StopReason(envelope, observation);
        var elapsed = clock.GetElapsedTime(startedAt).TotalMilliseconds;
        long? cliElapsed = double.IsFinite(elapsed) && elapsed >= 0 && elapsed < long.MaxValue
            ? (long)Math.Ceiling(elapsed) : null;
        return JsonSerializer.SerializeToElement(new
        {
            invocationUsageVersion = 1,
            invocationId = id.ToString("D"),
            campaignLineage = scope?.CampaignLineage,
            snapshotId = scope?.Snapshot.ExecutionCommitmentSha256,
            batchId = scope?.Batch.Identity,
            checkpointRevision = envelope.Revision,
            publicationOperationId = envelope.PublicationOperation,
            generationId = envelope.Generation,
            pullRequestUrl = envelope.Url,
            work = scope is null ? null : new
            {
                selectedTargets = selected!.Count,
                startedTargets = started.Count,
                resumedTargets = resumed.Count,
                acceptedTargets = accepted.Count,
                skippedTargets = skipped.Count,
                failedTargets = failed.Count,
                reconstructedTargets = reconstructed.Count,
                remainingTargets = progress!.Count(item => item.Kind != CampaignTargetProgressKind.AcceptedProposal),
                deferredTargets = scope.TargetProgress.Count(item => item.Kind == CampaignTargetProgressKind.Deferred),
            },
            calls = new
            {
                physicalProviderDispatches = observation?.PhysicalProviderDispatches ?? 0,
                logicalProviderCalls = observation is null ? 0 : observation.LogicalProviderCalls,
                retryDispatches = observation is null ? 0 : observation.RetryDispatches,
                uncorrelatedDispatches = observation?.UncorrelatedDispatches ?? 0,
                toolCalls = observation?.ToolCalls ?? 0,
                activeSent = observation?.ActiveSent ?? 0,
                activeReserved = observation?.ActiveReserved ?? 0,
            },
            tokens,
            accounting = observation is null ? null : new
            {
                input = observation.InputCharge,
                cachedInput = observation.CachedInputCharge,
                uncachedInput = observation.UncachedInputCharge,
                output = observation.OutputCharge,
                unsettledDispatches = observation.UnsettledDispatches,
                activeExposure = observation.ActiveExposure,
            },
            history = scope is null ? null : new
            {
                providerRequests = scope.LineageCharges.ProviderRequests,
                input = scope.LineageCharges.InputTokens,
                cachedInput = scope.LineageCharges.CachedInputTokens,
                uncachedInput = scope.LineageCharges.UncachedInputTokens,
                output = scope.LineageCharges.OutputTokens,
                reasoning = scope.LineageCharges.ReasoningTokens,
            },
            timing = new
            {
                cliElapsedMilliseconds = cliElapsed,
                allowanceElapsedMilliseconds = observation?.ElapsedMilliseconds,
            },
            termination = new
            {
                stopReason = stop,
                source = envelope.StopSource,
                allowanceStopReason = observation?.StopReason.ToString(),
                scribeFinishReason = finish?.ToString(),
            },
            completeness = observation?.PhysicalProviderDispatches > 0
                && new[] { tokens.input, tokens.cachedInput, tokens.uncachedInput, tokens.output, tokens.reasoning }.All(value => value.Observed is null)
                ? "unknown" : missing || scope is null || cliElapsed is null
                || observation is not null && observation.ElapsedMilliseconds is null || observation?.UnsettledDispatches > 0
                || observation?.UncorrelatedDispatches > 0 ? "partial" : "complete",
        }, JsonOptions);
    }

    private string StopReason(GitHubProposalEnvelope envelope, DocumentationScribeInvocationObservation? observation)
    {
        if (envelope.Outcome is "cancelled" or "timeout") return envelope.Outcome;
        var contextStop = finish == DocumentationScribeFinishReason.Context && envelope.Layer == "campaign"
            && envelope.StopSource is "campaign.provider-retryable" or "campaign.provider-terminal"
                or "campaign.unresolved" or "campaign.complete" or "campaign.batch-complete";
        if (envelope.Outcome is not ("published" or "published-partial" or "replayed" or "no-op" or "merged"
            or "awaiting-review" or "closed-unmerged" or "stale" or "conflict") && !contextStop) return "failure";
        if (envelope.StopSource == "closed-same-base") return "closed-same-base";
        if (envelope.StopSource is "changed-base" or "stale-publication") return "changed-base";
        if (envelope.Outcome == "awaiting-review") return "awaiting-review";
        if (envelope.StopSource == "campaign.target-limit") return "target-limit";
        if (observation?.StopReason is not (null or DocumentationScribeInvocationStopReason.None)
            || envelope.StopSource is "campaign.budget-exhausted" or "campaign.invocation-budget-exhausted") return "resource-limit";
        if (finish == DocumentationScribeFinishReason.Context
            && (envelope.Outcome is "published" or "published-partial" or "replayed" or "no-op" or "merged"
                || contextStop)) return "context-limit";
        if (envelope.Outcome is "conflict" or "stale" or "closed-unmerged") return "failure";
        return envelope.Outcome == "no-op" ? "no-op" : "completed";
    }
}
