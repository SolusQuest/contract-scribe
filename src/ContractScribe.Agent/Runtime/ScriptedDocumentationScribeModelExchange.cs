using System.Collections.Immutable;

namespace ContractScribe.Agent.Runtime;

internal enum ScriptedDocumentationScribeStepKind
{
    Response,
    Failure,
    WaitForCancellation,
    HeldResponse,
}

internal sealed class ScriptedDocumentationScribeStep
{
    private readonly DocumentationScribeModelResponse? response;
    private readonly TaskCompletionSource<DocumentationScribeModelResponse>? release;

    private ScriptedDocumentationScribeStep(
        ScriptedDocumentationScribeStepKind kind,
        DocumentationScribeModelResponse? response,
        TaskCompletionSource<DocumentationScribeModelResponse>? release)
    {
        Kind = kind;
        this.response = response;
        this.release = release;
    }

    internal ScriptedDocumentationScribeStepKind Kind { get; }

    internal static ScriptedDocumentationScribeStep Return(DocumentationScribeModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(ScriptedDocumentationScribeStepKind.Response, response, null);
    }

    internal static ScriptedDocumentationScribeStep Throw() =>
        new(ScriptedDocumentationScribeStepKind.Failure, null, null);

    internal static ScriptedDocumentationScribeStep WaitForCancellation() =>
        new(ScriptedDocumentationScribeStepKind.WaitForCancellation, null, null);

    internal static ScriptedDocumentationScribeStep Hold(DocumentationScribeModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(
            ScriptedDocumentationScribeStepKind.HeldResponse,
            response,
            new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal void Release()
    {
        if (Kind != ScriptedDocumentationScribeStepKind.HeldResponse || release is null || response is null)
        {
            throw new InvalidOperationException("Only a held response can be released.");
        }

        release.TrySetResult(response);
    }

    internal async ValueTask<DocumentationScribeModelResponse> ExecuteAsync(
        CancellationToken cancellationToken)
    {
        switch (Kind)
        {
            case ScriptedDocumentationScribeStepKind.Response:
                return response!;
            case ScriptedDocumentationScribeStepKind.Failure:
                throw new InvalidOperationException("The scripted model exchange failed.");
            case ScriptedDocumentationScribeStepKind.WaitForCancellation:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The cancellation wait completed unexpectedly.");
            case ScriptedDocumentationScribeStepKind.HeldResponse:
                return await release!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("The scripted model exchange step is invalid.");
        }
    }

    public override string ToString() => nameof(ScriptedDocumentationScribeStep);
}

internal sealed class ScriptedDocumentationScribeModelExchange : IDocumentationScribeModelExchange
{
    private readonly ImmutableArray<ScriptedDocumentationScribeStep> steps;
    private readonly object sync = new();
    private readonly List<DocumentationScribeModelRequest> requests = [];
    private int nextStep;

    internal ScriptedDocumentationScribeModelExchange(
        ImmutableArray<ScriptedDocumentationScribeStep> steps)
    {
        if (steps.IsDefaultOrEmpty || steps.Any(step => step is null))
        {
            throw new ArgumentException("At least one valid scripted step is required.", nameof(steps));
        }

        this.steps = steps;
    }

    internal ImmutableArray<DocumentationScribeModelRequest> Requests
    {
        get
        {
            lock (sync)
            {
                return requests.ToImmutableArray();
            }
        }
    }

    public ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ScriptedDocumentationScribeStep step;
        lock (sync)
        {
            requests.Add(request);
            if (nextStep >= steps.Length)
            {
                throw new InvalidOperationException("The scripted model exchange was exhausted.");
            }

            step = steps[nextStep++];
        }

        return step.ExecuteAsync(cancellationToken);
    }

    public override string ToString() => nameof(ScriptedDocumentationScribeModelExchange);
}
