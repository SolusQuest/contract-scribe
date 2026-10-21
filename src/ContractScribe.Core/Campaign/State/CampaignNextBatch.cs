namespace ContractScribe.Core;

public static partial class CampaignStateReducer
{
    public static CampaignTransitionResult AdvanceNoResultBatch(CampaignAcceptedCheckpoint checkpoint, CampaignWorkPlan nextPlan)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(nextPlan);
        var before = checkpoint.Artifact;
        var state = before.State;
        var expected = CampaignStateFactory.NextNoResultBatch(state, new(nextPlan.Batch.CreationQuota));
        if (expected is null || expected.Identity != nextPlan.Batch.Identity
            || state.Snapshot.ExecutionCommitmentSha256 != nextPlan.ExecutionCommitment
            || !state.Batch.CompleteTargets.SequenceEqual(nextPlan.Batch.CompleteTargets))
            return Reject(before, CampaignTransitionFailure.InvalidAuthority);
        try
        {
            var next = CampaignStateFactory.CreateValidated(state.ProductRevision, state.CampaignLineage,
                state.Snapshot, NextRevision(state.CheckpointRevision), state.ConfiguredCeilings,
                state.LineageCharges, state.WorkItems, nextPlan.Batch,
                knownCompletedOperations: state.KnownCompletedOperations,
                terminalOutcome: CampaignStateFactory.SelectBatchTerminal(nextPlan.Batch, state.WorkItems),
                predecessor: state.Predecessor, dispatchObservations: state.DispatchObservations,
                archivedDispatchPrefix: state.ArchivedDispatchPrefix);
            var transition = Applied(before, next);
            CampaignStateFactory.ValidateSuccessorHistory(before, transition.Artifact);
            return transition;
        }
        catch (CampaignStateValidationException exception) when (exception.Code == CampaignStateValidationCode.DocumentTooLarge)
        { return Reject(before, CampaignTransitionFailure.CheckpointCapacity); }
        catch (Exception exception) when (IsBoundedContractFailure(exception))
        { return Reject(before, CampaignTransitionFailure.InvalidAuthority); }
    }
}
