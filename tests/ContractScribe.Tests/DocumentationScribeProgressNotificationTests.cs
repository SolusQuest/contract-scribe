using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Agent.Prompting;

namespace ContractScribe.Tests;

public sealed partial class DocumentationScribeRuntimeTests
{
    [Fact]
    public async Task Host_progress_observes_invalid_batches_valid_siblings_and_finish_before_next_send()
    {
        var request = IncrementalRequest();
        var ids = request.ExecutableTargetIds;
        var invalid = IncrementalProposal(request, ids[1]);
        invalid["contentUnits"]![0]!["evidenceReferenceIds"]![0] = "evidence.foreign";
        var host = new ProgressNotificationHost();
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0])), (ids[1], invalid)),
            SubmitBatch(request, 2, (ids[2], IncrementalProposal(request, ids[2]))), FinishConversation(request));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Equal(3, exchange.Requests.Length);
        Assert.Equal(4, host.Progress.Count);
        var partial = host.Progress[0]; // Same physical acceptance as the first member ACK.
        Assert.Equal(1, partial.NextSequence);
        Assert.Equal(0, partial.RepairCount);
        Assert.Single(partial.Receipts);
        Assert.Empty(partial.Failures);
        var completed = host.Progress.Skip(1).ToArray();
        Assert.Equal(new[] { 2, 3, 3 }, completed.Select(item => item.NextSequence));
        Assert.All(completed, item => Assert.Equal(1, item.RepairCount));
        Assert.Single(completed[0].Receipts);
        Assert.Single(completed[0].Failures);
        Assert.Equal(2, completed[1].Receipts.Length);
        Assert.False(completed[1].Stopped);
        Assert.True(completed[2].Stopped);
        Assert.Equal(2, result.Submissions.Length);
        Assert.All(result.Submissions, item => Assert.Equal(DocumentationScribeAcknowledgementStage.DurablyRecorded, item.Stage));
        var scalar = JsonSerializer.Serialize(host.Progress);
        Assert.DoesNotContain("contentUnits", scalar, StringComparison.Ordinal);
        Assert.DoesNotContain("exact submission", scalar, StringComparison.Ordinal);
        Assert.DoesNotContain("repositoryContextRef", scalar, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_foreign_progress_acknowledgement_stops_without_erasing_a_durable_member(bool foreign)
    {
        var request = IncrementalRequest();
        var ids = request.ExecutableTargetIds;
        var host = new ProgressNotificationHost(progress => foreign
            ? ValueTask.FromResult<DocumentationScribeProgressAcknowledgement?>(new(new string('f', 64)))
            : ValueTask.FromResult<DocumentationScribeProgressAcknowledgement?>(null));
        var exchange = Script(SubmitBatch(request, 1, (ids[0], IncrementalProposal(request, ids[0]))),
            SubmitBatch(request, 2, (ids[1], IncrementalProposal(request, ids[1]))));
        var result = await ExecuteIncremental(request, exchange, EmptyRegistry(), host);
        Assert.Single(exchange.Requests);
        Assert.Single(host.Progress);
        Assert.Equal(DocumentationScribeFailureCode.Internal, Assert.IsType<DocumentationScribeFailureTerminal>(result.Stop).Code);
        Assert.Equal(DocumentationScribeAcknowledgementStage.DurablyRecorded, Assert.Single(result.Submissions).Stage);
    }

    [Fact]
    public async Task Pending_progress_notification_uses_the_original_cancellation_and_late_receipt_cannot_resume_the_agent()
    {
        var request = IncrementalRequest();
        var id = request.ExecutableTargetIds[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<DocumentationScribeProgressAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var host = new ProgressNotificationHost(progress =>
        {
            entered.TrySetResult();
            return new(pending.Task); // Deliberately ignores cooperative cancellation.
        });
        var exchange = Script(SubmitBatch(request, 1, (id, IncrementalProposal(request, id))), FinishConversation(request));
        var execution = ExecuteIncremental(request, exchange, EmptyRegistry(), host, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DocumentationScribeTerminalKind.Cancelled, result.Stop.Kind);
        Assert.Single(exchange.Requests);
        // The paired Host call has not returned either acknowledgement yet.
        // Previously accepted siblings are covered by the incremental matrix.
        Assert.Equal(DocumentationScribeAcknowledgementStage.Received, Assert.Single(result.Submissions).Stage);
        var bytes = DocumentationScribeRunResultWriter.Write(request, result);
        pending.SetResult(new(host.Progress.Single().ProgressSha256));
        Assert.True(bytes.AsSpan().SequenceEqual(DocumentationScribeRunResultWriter.Write(request, result).AsSpan()));
        Assert.Single(exchange.Requests);
    }

    private sealed class ProgressNotificationHost(
        Func<DocumentationScribeProgressNotification, ValueTask<DocumentationScribeProgressAcknowledgement?>>? observe = null)
        : IDocumentationScribeSubmissionHost
    {
        internal List<DocumentationScribeProgressNotification> Progress { get; } = [];
        public async ValueTask<DocumentationScribeSubmissionAcknowledgement?> AcknowledgeAsync(
            DocumentationScribeValidatedSubmission submission, DocumentationScribeProgressNotification progress,
            CancellationToken cancellationToken)
        {
            var acknowledged = await ObserveProgressAsync(progress, cancellationToken);
            return new(submission.ScribeRequestSha256, submission.TargetId, submission.ContentSha256,
                DocumentationScribeAcknowledgementStage.DurablyRecorded)
            { ProgressSha256 = acknowledged?.ProgressSha256 };
        }

        public ValueTask<DocumentationScribeProgressAcknowledgement?> ObserveProgressAsync(
            DocumentationScribeProgressNotification progress, CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return observe?.Invoke(progress)
                ?? ValueTask.FromResult<DocumentationScribeProgressAcknowledgement?>(new(progress.ProgressSha256));
        }
    }
}
