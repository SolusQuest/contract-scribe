using ContractScribe.Agent.Runtime;

namespace ContractScribe.Agent.Providers;

/// <summary>One actual send's closed, process-local diagnostics; never execution or usage authority.</summary>
public sealed class OpenAiCompatibleProviderObservation
{
    internal OpenAiCompatibleProviderObservation(int ordinal, DocumentationScribeModelRequest request,
        string? attemptId, DocumentationScribeModelFailure? failure, string? codecDisposition)
    {
        PhysicalDispatchOrdinal = ordinal;
        AttemptId = attemptId;
        AttemptNumber = request.AttemptNumber;
        ProviderRequestNumber = request.ProviderRequestNumber;
        FailureCode = failure is null ? null : DocumentationScribeModelFailure.CodeId(failure.Code);
        FailureOrigin = failure?.Origin is { } origin ? DocumentationScribeModelFailure.OriginId(origin) : null;
        HttpStatusCode = failure?.HttpStatusCode;
        CodecDisposition = codecDisposition;
    }

    public int PhysicalDispatchOrdinal { get; }
    public string? AttemptId { get; }
    public int AttemptNumber { get; }
    public int ProviderRequestNumber { get; }
    public string? FailureCode { get; }
    public string? FailureOrigin { get; }
    public int? HttpStatusCode { get; }
    public string? CodecDisposition { get; }
    public override string ToString() => nameof(OpenAiCompatibleProviderObservation);
}
