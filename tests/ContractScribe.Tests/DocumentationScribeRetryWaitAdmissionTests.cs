using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed partial class CampaignStateContractTests
{
    [Theory]
    [InlineData(250, "none")]
    [InlineData(1000, "none")]
    [InlineData(1001, "none")]
    [InlineData(750, "invocation")]
    [InlineData(1000, "lifetime")]
    [InlineData(250, "checkpoint")]
    public async Task Solus_runtime_retry_preparation_respects_minimum_wait_and_original_upper_bounds(
        int preparationMilliseconds, string stop)
    {
        var clock = new SolusDeadlineClock();
        using var cancellation = new CancellationTokenSource();
        var trial = await SolusTrialAsync(clock: clock, targetMilliseconds: 10000,
            invocationMilliseconds: stop == "invocation" ? 500 : 5000,
            checkpointAcceptanceMilliseconds: stop == "checkpoint" ? 100 : 5000,
            campaignMilliseconds: stop == "lifetime" ? 750 : null);
        var preparationReads = 0;
        var chargedBefore = trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed;
        byte[]? predecessor = null;
        trial.Store.BeforeRetryAdmissionRead = () =>
        {
            preparationReads++;
            Assert.Equal(1, trial.Store.Dispatches);
            Assert.Equal(1, trial.Store.Settlements);
            Assert.DoesNotContain(CampaignProviderHostPhase.RetryWait, trial.Store.AdmittedHostPhases);
            Assert.Equal(1000, trial.Coordinator.Scope.PendingRetryAfterMilliseconds);
            predecessor = trial.Store.Current.ExactUtf8Json.ToArray();
            clock.Advance(preparationMilliseconds);
        };
        var exchange = new ScriptedDocumentationScribeModelExchange([
            ScriptedDocumentationScribeStep.Return(new([], [],
                new(DocumentationScribeModelFailureCode.TransientUnavailable, retryAfterMilliseconds: 1000), usage: new(0, 0, 0, 0))),
            ScriptedDocumentationScribeStep.Return(new([], [new(C3Submission(trial.Scenario, trial.Request))], usage: new(0, 0, 0, 0))),
            C3FinishStep(trial.Request)]);
        var running = trial.Session(exchange).RunAsync(cancellation.Token);
        try
        {
            await Task.WhenAny(running, trial.Store.RetryWaitEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            if (stop == "none" && preparationMilliseconds < 1000 && !running.IsCompleted)
                clock.Advance(1000 - preparationMilliseconds);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, preparationReads);
            Assert.NotNull(predecessor);
            Assert.False(trial.Coordinator.CheckpointCancelledByHostDeadline,
                $"preparationReads={preparationReads}; physicalRequests={exchange.Requests.Length}; "
                + $"dispatches={trial.Store.Dispatches}; settlements={trial.Store.Settlements}; "
                + $"retryWaitWrites={trial.Store.AdmittedHostPhases.Count(phase => phase == CampaignProviderHostPhase.RetryWait)}; "
                + $"checkpointFailure={trial.Coordinator.CheckpointFailure}; "
                + $"currentEqualsPredecessor={predecessor.SequenceEqual(trial.Store.Current.ExactUtf8Json)}; "
                + $"writerEqualsPredecessor={predecessor.SequenceEqual(trial.Coordinator.LatestAcknowledgedArtifact.ExactUtf8Json)}");
            if (stop == "none")
            {
                Assert.Null(trial.Coordinator.CheckpointFailure);
                Assert.Equal(DocumentationScribeTerminalKind.Proposal, Assert.Single(result.Submissions).Result.Kind);
                Assert.Equal(3, exchange.Requests.Length);
                Assert.Equal(3, trial.Store.Dispatches);
                Assert.Equal(3, trial.Store.Settlements);
                Assert.Equal(1, trial.Coordinator.Scope.Allowance.Observe().RetryDispatches);
                Assert.Equal(0, trial.Coordinator.Scope.PendingRetryAfterMilliseconds);
                Assert.Equal(1, trial.Store.AdmittedHostPhases.Count(phase => phase == CampaignProviderHostPhase.RetryWait));
                Assert.Equal(1, trial.Store.CompletedHostReadbacks.Count(phase => phase == CampaignProviderHostPhase.RetryWait));
                Assert.Equal(Math.Max(1000, preparationMilliseconds), clock.GetTimestamp());
                Assert.Equal(Math.Max(1000, preparationMilliseconds),
                    trial.Store.Current.State.LineageCharges.ActiveElapsedMilliseconds.Observed - chargedBefore);
                Assert.Equal(trial.Store.Current.Sha256, trial.Coordinator.LatestAcknowledgedArtifact.Sha256);
                await trial.CompleteAsync(result);
            }
            else
            {
                Assert.Equal(CampaignCheckpointAcceptanceKind.Cancelled, trial.Coordinator.CheckpointFailure);
                Assert.Empty(result.Submissions);
                Assert.Single(exchange.Requests);
                Assert.Equal(1, trial.Store.Dispatches);
                Assert.Equal(1, trial.Store.Settlements);
                Assert.DoesNotContain(CampaignProviderHostPhase.RetryWait, trial.Store.AdmittedHostPhases);
                Assert.Equal(predecessor, trial.Store.Current.ExactUtf8Json.ToArray());
                Assert.Equal(predecessor, trial.Coordinator.LatestAcknowledgedArtifact.ExactUtf8Json.ToArray());
            }
        }
        finally
        {
            cancellation.Cancel();
            if (!running.IsCompleted) await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
