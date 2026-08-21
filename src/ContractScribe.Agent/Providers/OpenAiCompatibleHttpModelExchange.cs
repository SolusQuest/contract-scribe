using System.Buffers;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using ContractScribe.Agent.Runtime;

namespace ContractScribe.Agent.Providers;

public sealed class OpenAiCompatibleHttpModelExchange : IDocumentationScribeModelExchange, IDisposable
{
    private readonly OpenAiCompatibleHttpTransportOptions options;
    private readonly HttpMessageInvoker invoker;
    private int disposed;

    public OpenAiCompatibleHttpModelExchange(OpenAiCompatibleHttpTransportOptions options)
        : this(options, CreateProductionHandler(), disposeHandler: true)
    {
    }

    internal OpenAiCompatibleHttpModelExchange(
        OpenAiCompatibleHttpTransportOptions options,
        HttpMessageHandler handler,
        bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        this.options = options;
        invoker = new HttpMessageInvoker(
            new SanitizingHandler(handler, disposeHandler),
            disposeHandler: true);
    }

    public async ValueTask<DocumentationScribeModelResponse> SendAsync(
        DocumentationScribeModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.NetworkEnabled)
        {
            return Failure(
                DocumentationScribeModelFailureCode.Unsupported,
                origin: DocumentationScribeModelFailureOrigin.RequestPreparation);
        }

        OpenAiCompatiblePreparedRequest prepared;
        try
        {
            prepared = OpenAiCompatibleChatCompletionsCodec.Prepare(
                request,
                options.Model,
                options.RequestProfile);
        }
        catch (OpenAiCompatibleProtocolException exception)
        {
            return Failure(
                exception.Code,
                origin: DocumentationScribeModelFailureOrigin.RequestPreparation);
        }

        try
        {
            using var message = CreateMessage(prepared);
            using var response = await invoker.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ClassifyStatus(response);
            }

            if (response.Content.Headers.ContentEncoding.Count > 0
                || !IsJson(response.Content.Headers.ContentType))
            {
                return Failure(
                    DocumentationScribeModelFailureCode.Unsupported,
                    origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > OpenAiCompatibleChatCompletionsCodec.MaximumRawResponseUtf8Bytes)
            {
                return Failure(
                    DocumentationScribeModelFailureCode.MalformedResponse,
                    origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
            try
            {
                return OpenAiCompatibleChatCompletionsCodec.ParseResponse(body, prepared);
            }
            catch (OpenAiCompatibleProtocolException exception)
            {
                return Failure(
                    exception.Code,
                    origin: DocumentationScribeModelFailureOrigin.ResponseCodec,
                    continuationObservation: exception.ContinuationObservation);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OpenAiCompatibleProtocolException exception)
        {
            return Failure(
                exception.Code,
                origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
        }
        catch (OpenAiCompatibleSanitizedTransportException exception)
        {
            return Failure(
                exception.Code,
                origin: DocumentationScribeModelFailureOrigin.Transport);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DocumentationScribeModelFailureCode.TransientUnavailable,
                origin: DocumentationScribeModelFailureOrigin.Transport);
        }
        catch (HttpIOException exception)
        {
            return Failure(
                ClassifyStatus200BodyRead(exception.HttpRequestError),
                origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
        }
        catch (HttpRequestException exception)
        {
            return Failure(
                ClassifyStatus200BodyRead(exception.HttpRequestError),
                origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
        }
        catch (IOException)
        {
            return Failure(
                DocumentationScribeModelFailureCode.TransientUnavailable,
                origin: DocumentationScribeModelFailureOrigin.SuccessfulResponse);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Failure(DocumentationScribeModelFailureCode.PermanentUnavailable);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            invoker.Dispose();
        }
    }

    public override string ToString() => nameof(OpenAiCompatibleHttpModelExchange);

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        ActivityHeadersPropagator = null,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseDrainSize = 0,
        MaxResponseHeadersLength = 16,
        ResponseDrainTimeout = TimeSpan.Zero,
        UseCookies = false,
        UseProxy = false,
    };

    private HttpRequestMessage CreateMessage(OpenAiCompatiblePreparedRequest prepared)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new SingleSerializationContent(prepared.BodyUtf8),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.ExpectContinue = false;
        if (options.Credential is { } credential)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        return message;
    }

    private static DocumentationScribeModelResponse ClassifyStatus(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        var failure = code switch
        {
            401 or 403 => DocumentationScribeModelFailureCode.Authentication,
            408 or 425 or 500 or 502 or 503 or 504 => DocumentationScribeModelFailureCode.TransientUnavailable,
            429 => DocumentationScribeModelFailureCode.RateLimited,
            >= 100 and < 400 => DocumentationScribeModelFailureCode.Unsupported,
            >= 400 and < 600 => DocumentationScribeModelFailureCode.PermanentUnavailable,
            _ => DocumentationScribeModelFailureCode.PermanentUnavailable,
        };
        var retryAfter = failure is DocumentationScribeModelFailureCode.TransientUnavailable
            or DocumentationScribeModelFailureCode.RateLimited
            ? ParseRetryAfter(response)
            : null;
        return Failure(
            failure,
            retryAfter,
            DocumentationScribeModelFailureOrigin.HttpStatus,
            code);
    }

    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var materialized = values.ToArray();
        if (materialized.Length != 1
            || materialized[0].Length == 0
            || materialized[0].Any(value => value is < '0' or > '9')
            || !int.TryParse(materialized[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        try
        {
            var milliseconds = checked(seconds * 1_000);
            return milliseconds <= DocumentationScribeBoundary.MaximumRetryHintMilliseconds
                ? milliseconds
                : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static DocumentationScribeModelFailureCode ClassifyBeforeResponse(HttpRequestError error) =>
        error == HttpRequestError.ResponseEnded
            ? DocumentationScribeModelFailureCode.TransientUnavailable
            : ClassifyStatus200BodyRead(error);

    private static DocumentationScribeModelFailureCode ClassifyStatus200BodyRead(HttpRequestError error) => error switch
    {
        HttpRequestError.SecureConnectionError
            or HttpRequestError.UserAuthenticationError
            or HttpRequestError.ConfigurationLimitExceeded => DocumentationScribeModelFailureCode.PermanentUnavailable,
        HttpRequestError.InvalidResponse
            or HttpRequestError.ResponseEnded
            or HttpRequestError.HttpProtocolError => DocumentationScribeModelFailureCode.MalformedResponse,
        HttpRequestError.ExtendedConnectNotSupported
            or HttpRequestError.VersionNegotiationError => DocumentationScribeModelFailureCode.Unsupported,
        _ => DocumentationScribeModelFailureCode.TransientUnavailable,
    };

    private static bool IsJson(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType is not { } mediaType)
        {
            return false;
        }

        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = new byte[81_920];
        while (true)
        {
            var remaining = OpenAiCompatibleChatCompletionsCodec.MaximumRawResponseUtf8Bytes
                - writer.WrittenCount;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read > remaining)
            {
                throw new OpenAiCompatibleProtocolException(
                    DocumentationScribeModelFailureCode.MalformedResponse);
            }

            writer.Write(buffer.AsSpan(0, read));
        }

        if (contentLength is { } expected && expected != writer.WrittenCount)
        {
            throw new OpenAiCompatibleProtocolException(
                DocumentationScribeModelFailureCode.MalformedResponse);
        }

        return writer.WrittenSpan.ToArray();
    }

    private static DocumentationScribeModelResponse Failure(
        DocumentationScribeModelFailureCode code,
        int? retryAfterMilliseconds = null,
        DocumentationScribeModelFailureOrigin? origin = null,
        int? httpStatusCode = null,
        DocumentationScribeContinuationObservation continuationObservation =
            DocumentationScribeContinuationObservation.None) => new(
            ImmutableArray<DocumentationScribeModelToolCall>.Empty,
            ImmutableArray<DocumentationScribeModelTerminalSubmission>.Empty,
            new DocumentationScribeModelFailure(code, retryAfterMilliseconds, origin, httpStatusCode),
            usage: null,
            cache: null,
            cost: null,
            assistantContinuation: null,
            continuationObservation);

    private sealed class SanitizingHandler : DelegatingHandler
    {
        private readonly bool disposeInnerHandler;

        internal SanitizingHandler(HttpMessageHandler innerHandler, bool disposeInnerHandler)
        {
            InnerHandler = innerHandler;
            this.disposeInnerHandler = disposeInnerHandler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OpenAiCompatibleSanitizedCancellationException(cancellationToken);
            }
            catch (OpenAiCompatibleProtocolException)
            {
                throw;
            }
            catch (OpenAiCompatibleSanitizedTransportException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    ClassifyBeforeResponse(exception.HttpRequestError));
            }
            catch (HttpIOException exception)
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    ClassifyBeforeResponse(exception.HttpRequestError));
            }
            catch (OperationCanceledException)
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    DocumentationScribeModelFailureCode.TransientUnavailable);
            }
            catch (IOException)
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    DocumentationScribeModelFailureCode.TransientUnavailable);
            }
            catch (AuthenticationException)
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    DocumentationScribeModelFailureCode.PermanentUnavailable);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                throw new OpenAiCompatibleSanitizedTransportException(
                    DocumentationScribeModelFailureCode.PermanentUnavailable);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposeInnerHandler)
            {
                base.Dispose(disposing);
            }
        }
    }

    private sealed class SingleSerializationContent(byte[] body) : HttpContent
    {
        private int serializationCount;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref serializationCount) != 1)
            {
                throw new InvalidOperationException("The selected request body cannot be replayed.");
            }

            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }
}

internal sealed class OpenAiCompatibleSanitizedTransportException : Exception
{
    internal OpenAiCompatibleSanitizedTransportException(DocumentationScribeModelFailureCode code)
        : base("The selected provider transport failed.") => Code = code;

    internal DocumentationScribeModelFailureCode Code { get; }

    public override string ToString() => Message;
}

internal sealed class OpenAiCompatibleSanitizedCancellationException : OperationCanceledException
{
    internal OpenAiCompatibleSanitizedCancellationException(CancellationToken cancellationToken)
        : base("The selected provider request was canceled.", innerException: null, cancellationToken)
    {
    }

    public override string ToString() => Message;
}
