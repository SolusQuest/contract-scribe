using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Cli;

internal enum ChangedBaseCampaignReconciliationKind
{
    Accepted,
    Incompatible,
    InvalidConfiguration,
    Cancelled,
    CheckpointFailure,
}

internal sealed record ChangedBaseCampaignReconciliation(
    ChangedBaseCampaignReconciliationKind Kind,
    CampaignAcceptedCheckpoint? AcceptedCheckpoint = null,
    CampaignCheckpointAcceptanceKind? CheckpointFailure = null);

internal static class ChangedBaseCampaignReconciler
{
    internal static async Task<ChangedBaseCampaignReconciliation> ReconcileAsync(
        CampaignAcceptedCheckpoint predecessor,
        ICampaignCheckpointStore store,
        CampaignScribeExecutionCapability successorExecution,
        string styleConfigurationId,
        JsonElement validatedStyleConfigurationProjection,
        string inputIdentity,
        CampaignPlanningInput successorPlanningInput,
        CampaignWorkPlan successorPlan,
        Func<bool> configurationRevalidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(successorExecution);
        ArgumentException.ThrowIfNullOrWhiteSpace(styleConfigurationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdentity);
        ArgumentNullException.ThrowIfNull(successorPlanningInput);
        ArgumentNullException.ThrowIfNull(successorPlan);
        ArgumentNullException.ThrowIfNull(configurationRevalidator);

        CampaignTransitionResult transition;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var successorTemplate = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                successorExecution,
                inputIdentity,
                successorPlanningInput,
                successorPlan));
            var successorAuthority = CampaignCheckpointAcceptance.CreateInitialAuthority(successorTemplate);
            if (!configurationRevalidator())
            {
                return new(ChangedBaseCampaignReconciliationKind.InvalidConfiguration, predecessor);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transition = CampaignStateReducer.Supersede(
                predecessor.Artifact,
                predecessor,
                successorAuthority,
                successorExecution,
                styleConfigurationId,
                validatedStyleConfigurationProjection,
                inputIdentity,
                successorPlanningInput,
                successorPlan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ChangedBaseCampaignReconciliationKind.Cancelled, predecessor);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new(ChangedBaseCampaignReconciliationKind.Incompatible, predecessor);
        }

        if (transition.Kind == CampaignTransitionKind.Rejected)
        {
            return new(ChangedBaseCampaignReconciliationKind.Incompatible, predecessor);
        }

        var accepted = await CampaignCheckpointAcceptance.AcceptAsync(
            store,
            transition,
            cancellationToken).ConfigureAwait(false);
        if (accepted.Kind == CampaignCheckpointAcceptanceKind.Cancelled)
        {
            return await ReconcileCancelledAcceptanceAsync(
                store,
                predecessor,
                transition.Artifact).ConfigureAwait(false);
        }
        return accepted.Kind == CampaignCheckpointAcceptanceKind.Accepted
            && accepted.AcceptedCheckpoint is { } checkpoint
            ? new(ChangedBaseCampaignReconciliationKind.Accepted, checkpoint)
            : new(
                ChangedBaseCampaignReconciliationKind.CheckpointFailure,
                CheckpointFailure: accepted.Kind);
    }

    private static async Task<ChangedBaseCampaignReconciliation> ReconcileCancelledAcceptanceAsync(
        ICampaignCheckpointStore store,
        CampaignAcceptedCheckpoint predecessor,
        CampaignCheckpointArtifact intendedSuccessor)
    {
        CampaignCheckpointReadResult read;
        try
        {
            read = await store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new(
                ChangedBaseCampaignReconciliationKind.CheckpointFailure,
                CheckpointFailure: CampaignCheckpointAcceptanceKind.Unreadable);
        }

        if (read.Kind == CampaignCheckpointReadKind.NotFound)
        {
            return new(
                ChangedBaseCampaignReconciliationKind.CheckpointFailure,
                CheckpointFailure: CampaignCheckpointAcceptanceKind.Conflict);
        }

        var accepted = CampaignCheckpointAcceptance.AcceptCurrent(read);
        if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted
            || accepted.AcceptedCheckpoint is not { } current)
        {
            return new(
                ChangedBaseCampaignReconciliationKind.CheckpointFailure,
                CheckpointFailure: accepted.Kind);
        }
        if (ArtifactsEqual(current.Artifact, predecessor.Artifact))
        {
            return new(ChangedBaseCampaignReconciliationKind.Cancelled, current);
        }
        if (ArtifactsEqual(current.Artifact, intendedSuccessor))
        {
            return new(ChangedBaseCampaignReconciliationKind.Cancelled, current);
        }
        return new(
            ChangedBaseCampaignReconciliationKind.CheckpointFailure,
            current,
            CheckpointFailure: CampaignCheckpointAcceptanceKind.Conflict);
    }

    private static bool ArtifactsEqual(
        CampaignCheckpointArtifact left,
        CampaignCheckpointArtifact right) =>
        left.CheckpointRevision == right.CheckpointRevision
        && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
        && left.ExactUtf8Json.AsSpan().SequenceEqual(right.ExactUtf8Json.AsSpan());
}
