using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

internal static class DocumentationScribePhysicalExchange
{
    internal static async ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request, DocumentationScribeExecutionScope scope,
        IDocumentationScribePreparedPhysicalSend send,
        CancellationToken cancellationToken)
    {
        var bytes = request.DeterministicUtf8;
        var domain = Encoding.UTF8.GetBytes("contract-scribe/physical-model-request/v1\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(domain); hash.AppendData(bytes.AsSpan());
        var descriptor = new DocumentationScribeDispatchDescriptor(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            request.AttemptNumber, request.ProviderRequestNumber, request.OutputLimits.MaximumOutputTokens);
        var permit = await scope.ReserveProviderAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (permit is null) return new([], [], new(DocumentationScribeModelFailureCode.Unsupported,
            origin: DocumentationScribeModelFailureOrigin.RequestPreparation));
        DocumentationScribeModelResponse? response = null;
        var disposition = DocumentationScribeDispatchDisposition.Interrupted;
        try
        {
            if (permit.RemainingMilliseconds <= 0) throw new OperationCanceledException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(permit.RemainingMilliseconds), scope.Allowance.Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var task = send.SendAsync(permit, linked.Token).AsTask();
                try { response = await task.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!task.IsCompleted)
                { _ = DocumentationScribeRuntime.ObserveLateAsync(task); throw; }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                scope.ObserveDeadline(permit);
                if (permit.DeadlineOwner == DocumentationScribeDeadlineOwner.Provider)
                    response = new([], [], new(DocumentationScribeModelFailureCode.TransientUnavailable,
                        origin: DocumentationScribeModelFailureOrigin.Transport));
                else throw;
            }
            if (response is not null)
                disposition = response.Failure is { } failure
                    ? failure.IsTransient ? DocumentationScribeDispatchDisposition.RetryableFailure
                        : DocumentationScribeDispatchDisposition.TerminalFailure
                    : DocumentationScribeDispatchDisposition.Success;
            return response!;
        }
        finally
        {
            var usage = response?.Usage;
            var cost = response?.Cost;
            var observed = usage is null && cost is null ? null : new DocumentationScribeDispatchUsage(
                usage?.InputTokens, usage?.CachedInputTokens, usage?.UncachedInputTokens,
                usage?.OutputTokens, usage?.ReasoningTokens, cost?.CurrencyId, cost?.AmountMicrounits);
            var settlement = new DocumentationScribeDispatchSettlement(disposition, observed,
                response?.Failure?.RetryAfterMilliseconds ?? 0);
            if (!await scope.SettleProviderAsync(permit, settlement, CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("scribe.dispatch.settlement-unconfirmed");
        }
    }
}

internal interface IDocumentationScribePreparedPhysicalSend
{
    ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeInvocationProviderPermit permit, CancellationToken token);
}

internal sealed class DocumentationScribeExchangePhysicalSend(IDocumentationScribeModelExchange exchange,
    DocumentationScribeModelRequest request, DocumentationScribeExecutionScope scope) : IDocumentationScribePreparedPhysicalSend
{
    public ValueTask<DocumentationScribeModelResponse> SendAsync(DocumentationScribeInvocationProviderPermit permit, CancellationToken token) =>
        scope.BeginPhysicalDispatch(permit) ? exchange.SendAsync(request, token) : throw new OperationCanceledException(token);
}
