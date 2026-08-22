using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace ContractScribe.Agent.Providers;

public enum OpenAiCompatibleResponseCodecDisposition
{
    AcceptedTool,
    AcceptedTerminal,
    ResponseExceedsLimit,
    JsonInvalid,
    JsonDuplicateProperty,
    RootInvalid,
    ChoicesInvalid,
    ChoiceIndexInvalid,
    MessageInvalid,
    MessageRoleInvalid,
    MessageContentInvalid,
    MessageThinkingContentInvalid,
    MessageRefusalInvalid,
    ToolCallsInvalid,
    ToolCallIndexInvalid,
    ToolCallEnvelopeInvalid,
    ToolCallAliasInvalid,
    ArgumentsInvalid,
    TerminalMixed,
    UsageInvalid,
    FinishReasonUnsupported,
    FinishReasonInconsistent,
    TerminalArgumentsExceedsLimit,
    ContinuationMissing,
    ToolCallsExceedsLimit,
    ToolCallArgumentsExceedsLimit,
}

public sealed class OpenAiCompatibleResponseDiagnostic
{
    internal OpenAiCompatibleResponseDiagnostic(
        int providerRequestNumber,
        OpenAiCompatibleResponseCodecDisposition disposition)
    {
        ProviderRequestNumber = providerRequestNumber;
        CodecDisposition = OpenAiCompatibleResponseDiagnosticVocabulary.GetId(disposition);
    }

    public int ProviderRequestNumber { get; }

    public string CodecDisposition { get; }

    public override string ToString() => nameof(OpenAiCompatibleResponseDiagnostic);
}

public sealed class OpenAiCompatibleResponseDiagnosticCase
{
    internal OpenAiCompatibleResponseDiagnosticCase(
        ImmutableArray<OpenAiCompatibleResponseDiagnostic> responses,
        int providerRequestCount,
        string? failureCode)
    {
        Responses = responses;
        ProviderRequestCount = providerRequestCount;
        FailureCode = failureCode;
    }

    public IReadOnlyList<OpenAiCompatibleResponseDiagnostic> Responses { get; }

    public int ProviderRequestCount { get; }

    public string? FailureCode { get; }

    public override string ToString() => nameof(OpenAiCompatibleResponseDiagnosticCase);
}

public sealed class OpenAiCompatibleResponseDiagnostics : IDisposable
{
    private readonly OpenAiCompatibleLinuxResponseCapture? capture;
    private readonly List<OpenAiCompatibleResponseDiagnostic> responses = [];
    private int maximumProviderRequests;
    private int lastProviderRequestNumber;
    private bool active;
    private bool completedUnsafeCapture;
    private string? failureCode;
    private int disposed;

    private OpenAiCompatibleResponseDiagnostics(OpenAiCompatibleLinuxResponseCapture? capture)
    {
        this.capture = capture;
    }

    public static OpenAiCompatibleResponseDiagnostics CreateClosedObservations() => new(capture: null);

    [SupportedOSPlatform("linux")]
    public static OpenAiCompatibleResponseDiagnostics CreateUnsafeLinuxCapture(
        string captureDirectory,
        IReadOnlyCollection<string> forbiddenRoots) => new(
            OpenAiCompatibleLinuxResponseCapture.Create(captureDirectory, forbiddenRoots));

    public void BeginCase(int maximumProviderRequests)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (active
            || completedUnsafeCapture
            || maximumProviderRequests <= 0
            || maximumProviderRequests > 1_024)
        {
            Fail("evaluation.diagnostics.failed");
        }

        responses.Clear();
        failureCode = null;
        lastProviderRequestNumber = 0;
        this.maximumProviderRequests = maximumProviderRequests;
        active = true;
    }

    public OpenAiCompatibleResponseDiagnosticCase CompleteCase()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!active)
        {
            Fail("evaluation.diagnostics.failed");
        }

        active = false;
        completedUnsafeCapture = capture is not null;
        return new OpenAiCompatibleResponseDiagnosticCase(
            responses.ToImmutableArray(),
            lastProviderRequestNumber,
            failureCode);
    }

    internal void BeginProviderRequest(int providerRequestNumber)
    {
        if (!active
            || providerRequestNumber != lastProviderRequestNumber + 1
            || providerRequestNumber > maximumProviderRequests)
        {
            Fail("evaluation.diagnostics.failed");
        }

        lastProviderRequestNumber = providerRequestNumber;
    }

    internal async ValueTask ObserveAsync(
        int providerRequestNumber,
        OpenAiCompatibleResponseCodecDisposition disposition,
        ReadOnlyMemory<byte> responseBody,
        CancellationToken cancellationToken)
    {
        if (!active
            || providerRequestNumber != lastProviderRequestNumber
            || responses.Count > 0
                && responses[^1].ProviderRequestNumber >= providerRequestNumber)
        {
            Fail("evaluation.diagnostics.failed");
        }

        responses.Add(new OpenAiCompatibleResponseDiagnostic(providerRequestNumber, disposition));
        if (capture is null)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                await WriteLinuxAsync(
                    capture,
                    providerRequestNumber,
                    responseBody,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Fail("evaluation.capture.failed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            failureCode = "evaluation.capture.failed";
            throw new OpenAiCompatibleDiagnosticException(failureCode);
        }
    }

    [DoesNotReturn]
    internal OpenAiCompatibleResponseCodecDisposition FailMissingDisposition()
    {
        failureCode = "evaluation.diagnostics.failed";
        throw new OpenAiCompatibleDiagnosticException(failureCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0
            && capture is not null
            && OperatingSystem.IsLinux())
        {
            capture.Dispose();
        }
    }

    public override string ToString() => nameof(OpenAiCompatibleResponseDiagnostics);

    [SupportedOSPlatform("linux")]
    private static ValueTask WriteLinuxAsync(
        OpenAiCompatibleLinuxResponseCapture capture,
        int providerRequestNumber,
        ReadOnlyMemory<byte> responseBody,
        CancellationToken cancellationToken) => capture.WriteAsync(
            providerRequestNumber,
            responseBody,
            cancellationToken);

    [DoesNotReturn]
    private void Fail(string code)
    {
        failureCode = code;
        throw new OpenAiCompatibleDiagnosticException(code);
    }
}

internal static class OpenAiCompatibleResponseDiagnosticVocabulary
{
    internal static string GetId(OpenAiCompatibleResponseCodecDisposition disposition) => disposition switch
    {
        OpenAiCompatibleResponseCodecDisposition.AcceptedTool => "codec.accepted-tool",
        OpenAiCompatibleResponseCodecDisposition.AcceptedTerminal => "codec.accepted-terminal",
        OpenAiCompatibleResponseCodecDisposition.ResponseExceedsLimit => "codec.response.exceeds-limit",
        OpenAiCompatibleResponseCodecDisposition.JsonInvalid => "codec.json.invalid",
        OpenAiCompatibleResponseCodecDisposition.JsonDuplicateProperty => "codec.json.duplicate-property",
        OpenAiCompatibleResponseCodecDisposition.RootInvalid => "codec.root.invalid",
        OpenAiCompatibleResponseCodecDisposition.ChoicesInvalid => "codec.choices.invalid",
        OpenAiCompatibleResponseCodecDisposition.ChoiceIndexInvalid => "codec.choice.index.invalid",
        OpenAiCompatibleResponseCodecDisposition.MessageInvalid => "codec.message.invalid",
        OpenAiCompatibleResponseCodecDisposition.MessageRoleInvalid => "codec.message.role.invalid",
        OpenAiCompatibleResponseCodecDisposition.MessageContentInvalid => "codec.message.content.invalid",
        OpenAiCompatibleResponseCodecDisposition.MessageThinkingContentInvalid =>
            "codec.message.thinking-content.invalid",
        OpenAiCompatibleResponseCodecDisposition.MessageRefusalInvalid => "codec.message.refusal.invalid",
        OpenAiCompatibleResponseCodecDisposition.ToolCallsInvalid => "codec.tool-calls.invalid",
        OpenAiCompatibleResponseCodecDisposition.ToolCallIndexInvalid => "codec.tool-call.index.invalid",
        OpenAiCompatibleResponseCodecDisposition.ToolCallEnvelopeInvalid => "codec.tool-call.envelope.invalid",
        OpenAiCompatibleResponseCodecDisposition.ToolCallAliasInvalid => "codec.tool-call.alias.invalid",
        OpenAiCompatibleResponseCodecDisposition.ArgumentsInvalid => "codec.arguments.invalid",
        OpenAiCompatibleResponseCodecDisposition.TerminalMixed => "codec.terminal.mixed",
        OpenAiCompatibleResponseCodecDisposition.UsageInvalid => "codec.usage.invalid",
        OpenAiCompatibleResponseCodecDisposition.FinishReasonUnsupported =>
            "codec.finish-reason.unsupported",
        OpenAiCompatibleResponseCodecDisposition.FinishReasonInconsistent =>
            "codec.finish-reason.inconsistent",
        OpenAiCompatibleResponseCodecDisposition.TerminalArgumentsExceedsLimit =>
            "codec.terminal.arguments-exceeds-limit",
        OpenAiCompatibleResponseCodecDisposition.ContinuationMissing => "codec.continuation.missing",
        OpenAiCompatibleResponseCodecDisposition.ToolCallsExceedsLimit =>
            "codec.tool-calls.exceeds-limit",
        OpenAiCompatibleResponseCodecDisposition.ToolCallArgumentsExceedsLimit =>
            "codec.tool-call.arguments-exceeds-limit",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
    };
}

internal sealed class OpenAiCompatibleDiagnosticException : Exception
{
    internal OpenAiCompatibleDiagnosticException(string code)
        : base("The selected provider diagnostics failed.") => Code = code;

    internal string Code { get; }

    public override string ToString() => Message;
}
