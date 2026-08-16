using System.Collections.Immutable;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

public readonly struct DocumentationScribeToolDecodeResult<TRequest>
{
    private DocumentationScribeToolDecodeResult(bool isValid, TRequest? request)
    {
        IsValid = isValid;
        Request = request;
    }

    public bool IsValid { get; }

    public TRequest? Request { get; }

    public static DocumentationScribeToolDecodeResult<TRequest> Accepted(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DocumentationScribeToolDecodeResult<TRequest>(true, request);
    }

    public static DocumentationScribeToolDecodeResult<TRequest> Rejected() => new(false, default);

    public override string ToString() => nameof(DocumentationScribeToolDecodeResult<TRequest>);
}

public sealed class DocumentationScribeToolResultPayload
{
    public DocumentationScribeToolResultPayload(
        ReadOnlyMemory<byte> resultUtf8Json,
        ImmutableArray<DocumentationScribeDynamicEvidenceInput> dynamicEvidence)
    {
        ResultUtf8JsonStorage = DocumentationScribeBoundary.ValidateJson(
            resultUtf8Json,
            nameof(resultUtf8Json),
            DocumentationScribeContract.MaximumArtifactUtf8Bytes);
        if (dynamicEvidence.IsDefault
            || dynamicEvidence.Length > DocumentationScribeContract.MaximumReferences
            || dynamicEvidence.Any(item => item is null))
        {
            throw new ArgumentException(
                "Dynamic evidence must be initialized and bounded.",
                nameof(dynamicEvidence));
        }

        DynamicEvidence = dynamicEvidence;
    }

    public ReadOnlyMemory<byte> ResultUtf8Json => ResultUtf8JsonStorage.AsMemory();

    public ImmutableArray<DocumentationScribeDynamicEvidenceInput> DynamicEvidence { get; }

    internal ImmutableArray<byte> ResultUtf8JsonStorage { get; }

    public override string ToString() => nameof(DocumentationScribeToolResultPayload);
}

public readonly struct DocumentationScribeToolEncodeResult
{
    private DocumentationScribeToolEncodeResult(
        bool isValid,
        DocumentationScribeToolResultPayload? payload)
    {
        IsValid = isValid;
        Payload = payload;
    }

    public bool IsValid { get; }

    public DocumentationScribeToolResultPayload? Payload { get; }

    public static DocumentationScribeToolEncodeResult Accepted(
        DocumentationScribeToolResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new DocumentationScribeToolEncodeResult(true, payload);
    }

    public static DocumentationScribeToolEncodeResult Rejected() => new(false, null);

    public override string ToString() => nameof(DocumentationScribeToolEncodeResult);
}

public interface IDocumentationScribeToolCodec<TRequest, TResult>
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    DocumentationScribeToolDecodeResult<TRequest> DecodeArguments(ReadOnlyMemory<byte> argumentsUtf8Json);

    DocumentationScribeToolEncodeResult EncodeResult(TRequest request, TResult result);
}

public sealed class DocumentationScribeToolRegistryBuilder
{
    private readonly string toolPolicyId;
    private readonly List<ToolRegistration> registrations = [];
    private bool isBuilt;
    private long registrationUtf8Bytes;

    public DocumentationScribeToolRegistryBuilder(string toolPolicyId) =>
        this.toolPolicyId = DocumentationScribeBoundary.ValidateIdentifier(toolPolicyId, nameof(toolPolicyId));

    public DocumentationScribeToolRegistryBuilder Add<TRequest, TResult>(
        IDocumentationScribeToolDescriptor<TRequest, TResult> descriptor,
        IDocumentationScribeToolPort<TRequest, TResult> port,
        IDocumentationScribeToolCodec<TRequest, TResult> codec,
        string description,
        ReadOnlyMemory<byte> inputSchemaUtf8Json,
        int maximumCallsPerRun)
        where TRequest : IDocumentationScribeToolRequest<TResult>
        where TResult : IDocumentationScribeToolResult
    {
        if (isBuilt)
        {
            throw new InvalidOperationException("The tool registry is already frozen.");
        }

        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(codec);
        if (maximumCallsPerRun is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCallsPerRun));
        }

        if (registrations.Count >= DocumentationScribeBoundary.MaximumToolCallsPerResponse)
        {
            throw new ArgumentException("The tool registry is outside the product boundary.", nameof(descriptor));
        }

        string operationId;
        try
        {
            operationId = DocumentationScribeBoundary.ValidateIdentifier(
                descriptor.OperationId,
                nameof(descriptor));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ArgumentException("The tool descriptor is not valid.", nameof(descriptor));
        }

        if (string.Equals(
                operationId,
                DocumentationScribeBoundary.TerminalOperationId,
                StringComparison.OrdinalIgnoreCase)
            || registrations.Any(registration => string.Equals(
            registration.OperationId,
            operationId,
            StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A tool operation is reserved or already registered.", nameof(descriptor));
        }

        var normalizedDescription = DocumentationScribeBoundary.NormalizeText(
            description,
            nameof(description),
            DocumentationScribeBoundary.MaximumToolDescriptionUtf8Bytes);
        ImmutableArray<byte> normalizedSchema;
        try
        {
            var boundedSchema = DocumentationScribeBoundary.ValidateJson(
                inputSchemaUtf8Json,
                nameof(inputSchemaUtf8Json),
                DocumentationScribeBoundary.MaximumToolSchemaUtf8Bytes);
            normalizedSchema = CanonicalJson.Normalize(boundedSchema.AsMemory());
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ArgumentException("The tool schema is not valid.", nameof(inputSchemaUtf8Json));
        }

        long prospectiveRegistrationUtf8Bytes;
        try
        {
            prospectiveRegistrationUtf8Bytes = checked(
                registrationUtf8Bytes
                + operationId.Length
                + System.Text.Encoding.UTF8.GetByteCount(normalizedDescription)
                + normalizedSchema.Length);
        }
        catch (OverflowException)
        {
            throw new ArgumentException("The tool registry is outside the product boundary.", nameof(descriptor));
        }

        if (prospectiveRegistrationUtf8Bytes > DocumentationScribeBoundary.MaximumLogicalRequestUtf8Bytes)
        {
            throw new ArgumentException("The tool registry is outside the product boundary.", nameof(descriptor));
        }

        registrationUtf8Bytes = prospectiveRegistrationUtf8Bytes;
        registrations.Add(new TypedToolRegistration<TRequest, TResult>(
            operationId,
            normalizedDescription,
            normalizedSchema,
            maximumCallsPerRun,
            port,
            codec));
        return this;
    }

    public DocumentationScribeToolRegistry Build()
    {
        if (isBuilt)
        {
            throw new InvalidOperationException("The tool registry is already frozen.");
        }

        isBuilt = true;
        return new DocumentationScribeToolRegistry(
            toolPolicyId,
            registrations.OrderBy(registration => registration.OperationId, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    public override string ToString() => nameof(DocumentationScribeToolRegistryBuilder);
}

public sealed class DocumentationScribeToolRegistry
{
    internal DocumentationScribeToolRegistry(
        string toolPolicyId,
        ImmutableArray<ToolRegistration> registrations)
    {
        ToolPolicyId = toolPolicyId;
        Registrations = registrations;
        Definitions = registrations.Select(registration => registration.Definition).ToImmutableArray();
    }

    public string ToolPolicyId { get; }

    public ImmutableArray<DocumentationScribeModelToolDefinition> Definitions { get; }

    internal ImmutableArray<ToolRegistration> Registrations { get; }

    internal int FindRegistrationIndex(string operationId)
    {
        for (var index = 0; index < Registrations.Length; index++)
        {
            if (string.Equals(Registrations[index].OperationId, operationId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public override string ToString() => nameof(DocumentationScribeToolRegistry);
}

internal abstract class ToolRegistration
{
    protected ToolRegistration(
        string operationId,
        string description,
        ImmutableArray<byte> schemaUtf8,
        int maximumCallsPerRun)
    {
        OperationId = operationId;
        MaximumCallsPerRun = maximumCallsPerRun;
        Definition = new DocumentationScribeModelToolDefinition(
            operationId,
            description,
            CanonicalJson.AsString(schemaUtf8));
    }

    internal string OperationId { get; }

    internal int MaximumCallsPerRun { get; }

    internal DocumentationScribeModelToolDefinition Definition { get; }

    internal abstract PreparedToolCall Prepare(DocumentationScribeModelToolCall call);
}

internal sealed class TypedToolRegistration<TRequest, TResult> : ToolRegistration
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    private readonly IDocumentationScribeToolPort<TRequest, TResult> port;
    private readonly IDocumentationScribeToolCodec<TRequest, TResult> codec;

    internal TypedToolRegistration(
        string operationId,
        string description,
        ImmutableArray<byte> schemaUtf8,
        int maximumCallsPerRun,
        IDocumentationScribeToolPort<TRequest, TResult> port,
        IDocumentationScribeToolCodec<TRequest, TResult> codec)
        : base(operationId, description, schemaUtf8, maximumCallsPerRun)
    {
        this.port = port;
        this.codec = codec;
    }

    internal override PreparedToolCall Prepare(DocumentationScribeModelToolCall call)
    {
        DocumentationScribeToolDecodeResult<TRequest> decoded;
        try
        {
            decoded = codec.DecodeArguments(call.ArgumentsUtf8Json);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ToolBoundaryInternalException();
        }

        if (!decoded.IsValid || decoded.Request is null)
        {
            throw new ToolProtocolException(call.OperationId);
        }

        return new TypedPreparedToolCall<TRequest, TResult>(call, decoded.Request, port, codec);
    }
}

internal abstract class PreparedToolCall
{
    protected PreparedToolCall(DocumentationScribeModelToolCall call) => Call = call;

    internal DocumentationScribeModelToolCall Call { get; }

    internal abstract ValueTask<ToolInvocationResult> InvokeAsync(CancellationToken cancellationToken);
}

internal sealed class TypedPreparedToolCall<TRequest, TResult> : PreparedToolCall
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    private readonly TRequest request;
    private readonly IDocumentationScribeToolPort<TRequest, TResult> port;
    private readonly IDocumentationScribeToolCodec<TRequest, TResult> codec;

    internal TypedPreparedToolCall(
        DocumentationScribeModelToolCall call,
        TRequest request,
        IDocumentationScribeToolPort<TRequest, TResult> port,
        IDocumentationScribeToolCodec<TRequest, TResult> codec)
        : base(call)
    {
        this.request = request;
        this.port = port;
        this.codec = codec;
    }

    internal override async ValueTask<ToolInvocationResult> InvokeAsync(CancellationToken cancellationToken)
    {
        TResult result;
        try
        {
            result = await port.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ToolBoundaryInternalException();
        }

        if (result is null || result.Outcome is null || !DocumentationScribeBoundary.IsKnownOutcome(result.Outcome))
        {
            throw new ToolProtocolException(Call.OperationId);
        }

        var outcome = result.Outcome;
        if (outcome == DocumentationScribeToolOutcome.BudgetExhausted
            || outcome == DocumentationScribeToolOutcome.TimedOut
            || outcome == DocumentationScribeToolOutcome.Cancelled
            || outcome == DocumentationScribeToolOutcome.Failure)
        {
            return new ToolInvocationResult(outcome, default, []);
        }

        DocumentationScribeToolEncodeResult encoded;
        try
        {
            encoded = codec.EncodeResult(request, result);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ToolBoundaryInternalException();
        }

        if (!encoded.IsValid || encoded.Payload is null)
        {
            throw new ToolProtocolException(Call.OperationId);
        }

        ImmutableArray<byte> normalized;
        try
        {
            normalized = CanonicalJson.Normalize(encoded.Payload.ResultUtf8Json);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new ToolProtocolException(Call.OperationId);
        }

        return new ToolInvocationResult(
            result.Outcome,
            normalized,
            encoded.Payload.DynamicEvidence);
    }
}

internal readonly record struct ToolInvocationResult(
    DocumentationScribeToolOutcome Outcome,
    ImmutableArray<byte> ResultUtf8Json,
    ImmutableArray<DocumentationScribeDynamicEvidenceInput> DynamicEvidence);

internal sealed class ToolProtocolException : Exception
{
    internal ToolProtocolException(string referenceId) : base("The tool boundary rejected an operation.") =>
        ReferenceId = referenceId;

    internal string ReferenceId { get; }
}

internal sealed class ToolBoundaryInternalException : Exception
{
    internal ToolBoundaryInternalException() : base("The tool boundary failed.")
    {
    }
}
