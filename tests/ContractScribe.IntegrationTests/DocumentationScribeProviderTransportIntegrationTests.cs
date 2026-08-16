using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;

namespace ContractScribe.IntegrationTests;

public sealed class DocumentationScribeProviderTransportIntegrationTests
{
    private const string AttemptId = "scribe-attempt.0123456789abcdef0123456789abcdef";
    private static readonly byte[] ToolSchema = """
        {"additionalProperties":false,"properties":{"referenceId":{"type":"string"}},"required":["referenceId"],"type":"object"}
        """u8.ToArray();

    [Theory]
    [InlineData("skip-result.json", DocumentationScribeTerminalKind.Skip)]
    [InlineData("proposal-result.json", DocumentationScribeTerminalKind.Proposal)]
    public async Task Runtime_completes_tool_result_terminal_paths_through_real_http(
        string fixture,
        DocumentationScribeTerminalKind expectedKind)
    {
        var terminal = ReadTerminal(fixture);
        var firstResponse = Completion("cs_tool_000", "call.one", "{\"referenceId\":\"one\"}");
        var secondResponse = Completion("cs_terminal", "call.terminal", Encoding.UTF8.GetString(terminal));
        await using var server = new LoopbackServer(firstResponse, secondResponse);
        var request = Request();
        var port = new SyntheticPort();
        var registry = Registry(request.ToolPolicyId, port);
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                server.Endpoint,
                "synthetic-model-v1",
                networkEnabled: true));
        var runtime = new DocumentationScribeRuntime(
            exchange,
            registry,
            new DocumentationScribeRuntimeOptions(
                "provider.direct-http.synthetic.v1",
                "model.synthetic.v1",
                "scribe-protocol.openai-compatible.v1"));

        var result = await runtime.RunAsync(request, Attempt(), Prompt(request));
        await server.Completion;

        Assert.Equal(expectedKind, result.Terminal.Kind);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(1, result.RunEnvelope.ToolRoundCount);
        Assert.Equal("one", Assert.Single(port.References));
        Assert.Equal(2, server.Requests.Length);
        using (var first = JsonDocument.Parse(server.Requests[0].Body))
        {
            Assert.Equal(5, first.RootElement.GetProperty("messages").GetArrayLength());
        }

        using (var second = JsonDocument.Parse(server.Requests[1].Body))
        {
            var messages = second.RootElement.GetProperty("messages");
            Assert.Equal(7, messages.GetArrayLength());
            Assert.Equal("assistant", messages[5].GetProperty("role").GetString());
            Assert.Equal("tool", messages[6].GetProperty("role").GetString());
            using var content = JsonDocument.Parse(messages[6].GetProperty("content").GetString()!);
            Assert.Equal(DocumentationScribeToolOutcome.Complete.Id,
                content.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("one", content.RootElement.GetProperty("result").GetProperty("referenceId").GetString());
        }

        Assert.All(server.Requests, observed =>
        {
            Assert.Equal("POST /v1/chat/completions HTTP/1.1", observed.RequestLine);
            Assert.DoesNotContain("Authorization:", observed.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("traceparent:", observed.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Content-Type: application/json; charset=utf-8", observed.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Accept: application/json", observed.Headers, StringComparison.OrdinalIgnoreCase);
        });

        if (expectedKind == DocumentationScribeTerminalKind.Proposal)
        {
            var proposal = Assert.IsType<DocumentationScribeProposalTerminal>(result.Terminal);
            Assert.Equal("M:Synthetic.Widget.Run(System.String)",
                proposal.Target.SymbolRef.DocumentationCommentId);
            Assert.Equal(
                [DocumentationScribeContentUnitKind.Summary, DocumentationScribeContentUnitKind.Parameter,
                    DocumentationScribeContentUnitKind.Return],
                proposal.ContentUnits.Select(unit => unit.Kind));
            Assert.Equal(
                ["evidence.summary", "evidence.parameter", "evidence.return"],
                proposal.ContentUnits.SelectMany(unit => unit.EvidenceReferenceIds));
        }
    }

    [Fact]
    public async Task Real_handler_returns_redirect_without_following_it()
    {
        await using var server = new LoopbackServer(
            "",
            statusLine: "HTTP/1.1 302 Found",
            extraHeaders: "Location: http://127.0.0.1:1/changed\r\n");
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion;

        Assert.Equal(DocumentationScribeModelFailureCode.Unsupported, response.Failure!.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Real_handler_propagates_body_read_cancellation()
    {
        await using var server = new HoldingBodyServer();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));
        using var cancellation = new CancellationTokenSource();
        var pending = exchange.SendAsync(await ModelRequestAsync(), cancellation.Token).AsTask();
        await server.HeadersSent.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.CancellationObserved);
    }

    [Fact]
    public async Task Real_handler_rejects_declared_oversize_without_draining_the_body()
    {
        await using var server = new BoundaryServer(async (stream, state) =>
        {
            var length = SelectedRawResponseUtf8Bytes + 1;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n\r\n"));
            await state.ObserveClientCloseAsync(stream);
        });
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.True(server.ClientClosedBeforeBody);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Real_handler_stops_at_chunked_max_plus_one_and_does_not_drain()
    {
        await using var server = new BoundaryServer(async (stream, state) =>
        {
            var length = SelectedRawResponseUtf8Bytes + 1;
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray());
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{length:X}\r\n"));
            var block = new byte[81_920];
            var remaining = length;
            while (remaining > 0)
            {
                var count = Math.Min(block.Length, remaining);
                await stream.WriteAsync(block.AsMemory(0, count));
                remaining -= count;
            }

            await stream.WriteAsync("\r\n"u8.ToArray());
            await state.ObserveClientCloseAsync(stream);
        });
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.True(server.ClientClosedBeforeBody);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Premature_real_response_eof_is_malformed_and_not_retried_by_runtime()
    {
        var body = Completion("cs_terminal", "call.terminal", Encoding.UTF8.GetString(ReadTerminal("skip-result.json")));
        await using var server = new BoundaryServer(async (stream, _) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length + 10}\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        var request = Request();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));
        var runtime = new DocumentationScribeRuntime(
            exchange,
            Registry(request.ToolPolicyId, new SyntheticPort()),
            new DocumentationScribeRuntimeOptions("provider.direct-http.synthetic.v1", "model.synthetic.v1", "protocol.v1"));

        var result = await runtime.RunAsync(request, Attempt(), Prompt(request));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeFailureCode.Provider,
            Assert.IsType<DocumentationScribeFailureTerminal>(result.Terminal).Code);
        Assert.Equal(1, result.RunEnvelope.ProviderRequestCount);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Premature_chunked_response_is_malformed()
    {
        await using var server = new BoundaryServer(async (stream, _) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{"u8.ToArray());
        });
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeModelFailureCode.MalformedResponse, response.Failure!.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Request_body_is_not_replayed_after_a_retry_eligible_socket_failure()
    {
        await using var server = new ReplayFaultServer();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeModelFailureCode.TransientUnavailable, response.Failure!.Code);
        Assert.Equal(1, server.CompleteRequestBodies);
        Assert.InRange(server.AcceptedConnections, 1, 2);
    }

    [Fact]
    public async Task Runtime_owns_retry_after_pre_response_eof_without_transport_body_replay()
    {
        var terminal = Completion(
            "cs_terminal",
            "call.terminal",
            Encoding.UTF8.GetString(ReadTerminal("skip-result.json")));
        await using var server = new ReplayFaultServer(terminal);
        var request = Request();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(server.Endpoint, "model", networkEnabled: true));
        var runtime = new DocumentationScribeRuntime(
            exchange,
            Registry(request.ToolPolicyId, new SyntheticPort()),
            new DocumentationScribeRuntimeOptions("provider.direct-http.synthetic.v1", "model.synthetic.v1", "protocol.v1"));

        var result = await runtime.RunAsync(request, Attempt(), Prompt(request));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeTerminalKind.Skip, result.Terminal.Kind);
        Assert.Equal(2, result.RunEnvelope.ProviderRequestCount);
        Assert.Equal(2, server.CompleteRequestBodies);
        Assert.InRange(server.AcceptedConnections, 2, 3);
    }

    [Fact]
    public async Task Production_handler_rejects_self_signed_tls_without_disclosing_credential()
    {
        const string marker = "tls-credential-marker";
        using var observations = new NetworkEventCollector();
        await using var server = new SelfSignedTlsServer();
        using var exchange = new OpenAiCompatibleHttpModelExchange(
            new OpenAiCompatibleHttpTransportOptions(
                server.Endpoint,
                "model",
                networkEnabled: true,
                marker));

        var response = await exchange.SendAsync(await ModelRequestAsync(), CancellationToken.None);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DocumentationScribeModelFailureCode.PermanentUnavailable, response.Failure!.Code);
        Assert.DoesNotContain(marker, response.Failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, observations.Text, StringComparison.Ordinal);
        Assert.False(server.HttpRequestObserved);
    }

    private static byte[] Completion(string alias, string callId, string arguments) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new { name = alias, arguments },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
        });

    private static int SelectedRawResponseUtf8Bytes => checked(
        6 * DocumentationScribeContract.MaximumArtifactUtf8Bytes
        + 128 * (6 * 1_024 + 512)
        + 65_536);

    private static async Task<DocumentationScribeModelRequest> ModelRequestAsync()
    {
        var tool = new DocumentationScribeToolRegistryBuilder("tool-policy.read-only.v1");
        tool.Add(
            new SyntheticDescriptor(),
            new SyntheticPort(),
            new SyntheticCodec(),
            "Reads bounded synthetic evidence.",
            ToolSchema,
            maximumCallsPerRun: 4);
        var request = Request();
        var prompt = Prompt(request);
        var capture = new CaptureExchange();
        var runtime = new DocumentationScribeRuntime(
            capture,
            tool.Build(),
            new DocumentationScribeRuntimeOptions("provider.synthetic.v1", "model.synthetic.v1", "protocol.v1"));
        var task = runtime.RunAsync(request, Attempt(), prompt);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.Cancellation.Cancel();
        _ = await task;

        return capture.Request ?? throw new InvalidOperationException("The runtime request was not captured.");
    }

    private static DocumentationScribeRequest Request()
    {
        var parsed = DocumentationScribeValidation.ParseRequest(ReadFixture("request.json"));
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static DocumentationScribePromptInput Prompt(DocumentationScribeRequest request)
    {
        var context = request.ContextReferences.Select(reference => new DocumentationScribeContextContent(
            reference.ContextReferenceId,
            reference.Kind,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('c', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        var evidence = request.EvidenceReferences.Select(reference => new DocumentationScribeEvidenceContent(
            reference.EvidenceReferenceId,
            reference.Authority,
            reference.ContentSha256,
            reference.IncludedUtf8ByteCount,
            reference.IsTruncated,
            new string('e', reference.IncludedUtf8ByteCount))).ToImmutableArray();
        return new DocumentationScribePromptInput(context, evidence);
    }

    private static DocumentationScribeAttemptId Attempt()
    {
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        return attempt;
    }

    private static DocumentationScribeToolRegistry Registry(string toolPolicyId, SyntheticPort port)
    {
        var builder = new DocumentationScribeToolRegistryBuilder(toolPolicyId);
        builder.Add(
            new SyntheticDescriptor(),
            port,
            new SyntheticCodec(),
            "Reads bounded synthetic evidence.",
            ToolSchema,
            maximumCallsPerRun: 4);
        return builder.Build();
    }

    private static byte[] ReadTerminal(string fixture)
    {
        using var document = JsonDocument.Parse(ReadFixture(fixture));
        return Encoding.UTF8.GetBytes(document.RootElement.GetProperty("terminal").GetRawText());
    }

    private static byte[] ReadFixture(string name) => File.ReadAllBytes(Path.Join(
        FindRepositoryRoot(), "tests", "fixtures", "documentation-scribe", "v1", "valid", name));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record SyntheticRequest(string ReferenceId) : IDocumentationScribeToolRequest<SyntheticResult>;

    private sealed record SyntheticResult(
        DocumentationScribeToolOutcome Outcome,
        string ReferenceId) : IDocumentationScribeToolResult;

    private sealed class SyntheticDescriptor : IDocumentationScribeToolDescriptor<SyntheticRequest, SyntheticResult>
    {
        public string OperationId => "tool.read";
    }

    private sealed class SyntheticPort : IDocumentationScribeToolPort<SyntheticRequest, SyntheticResult>
    {
        private readonly List<string> references = [];

        internal ImmutableArray<string> References => [.. references];

        public ValueTask<SyntheticResult> InvokeAsync(
            SyntheticRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Add(request.ReferenceId);
            return ValueTask.FromResult(new SyntheticResult(
                DocumentationScribeToolOutcome.Complete,
                request.ReferenceId));
        }
    }

    private sealed class SyntheticCodec : IDocumentationScribeToolCodec<SyntheticRequest, SyntheticResult>
    {
        public DocumentationScribeToolDecodeResult<SyntheticRequest> DecodeArguments(
            ReadOnlyMemory<byte> argumentsUtf8Json)
        {
            using var document = JsonDocument.Parse(argumentsUtf8Json);
            return DocumentationScribeToolDecodeResult<SyntheticRequest>.Accepted(
                new SyntheticRequest(document.RootElement.GetProperty("referenceId").GetString()!));
        }

        public DocumentationScribeToolEncodeResult EncodeResult(
            SyntheticRequest request,
            SyntheticResult result) => DocumentationScribeToolEncodeResult.Accepted(
                new DocumentationScribeToolResultPayload(
                    JsonSerializer.SerializeToUtf8Bytes(new { referenceId = result.ReferenceId }),
                    ImmutableArray<DocumentationScribeDynamicEvidenceInput>.Empty));
    }

    private sealed class CaptureExchange : IDocumentationScribeModelExchange
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenSource Cancellation { get; } = new();

        internal DocumentationScribeModelRequest? Request { get; private set; }

        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Started.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Cancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            throw new InvalidOperationException();
        }
    }

    private sealed class BoundaryServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Func<NetworkStream, BoundaryServer, Task> writeResponse;
        private readonly Task serverTask;
        private readonly List<ObservedRequest> requests = [];

        internal BoundaryServer(Func<NetworkStream, BoundaryServer, Task> writeResponse)
        {
            this.writeResponse = writeResponse;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal ImmutableArray<ObservedRequest> Requests => [.. requests];

        internal bool ClientClosedBeforeBody { get; private set; }

        internal Task Completion => serverTask;

        internal async Task ObserveClientCloseAsync(NetworkStream stream)
        {
            var buffer = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                ClientClosedBeforeBody = await stream.ReadAsync(buffer, timeout.Token) == 0;
            }
            catch (IOException)
            {
                // A reset is also conclusive evidence that the client closed instead of draining.
                ClientClosedBeforeBody = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Listener shutdown is the expected cleanup path when no client connected.
            }
        }

        private async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            requests.Add(await ReadRequestAsync(stream));
            await writeResponse(stream, this);
        }
    }

    private sealed class ReplayFaultServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource disposal = new();
        private readonly byte[]? responseAfterSecondCompleteBody;
        private readonly Task serverTask;

        internal ReplayFaultServer(byte[]? responseAfterSecondCompleteBody = null)
        {
            this.responseAfterSecondCompleteBody = responseAfterSecondCompleteBody;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal int AcceptedConnections { get; private set; }

        internal int CompleteRequestBodies { get; private set; }

        internal Task Completion => serverTask;

        public async ValueTask DisposeAsync()
        {
            disposal.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Cancellation and listener shutdown are the expected cleanup paths.
            }

            disposal.Dispose();
        }

        private async Task ServeAsync()
        {
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(disposal.Token);
            while (!inactivity.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(inactivity.Token);
                }
                catch (OperationCanceledException) when (inactivity.IsCancellationRequested)
                {
                    break;
                }

                using (client)
                {
                    AcceptedConnections++;
                    await using var stream = client.GetStream();
                    try
                    {
                        _ = await ReadRequestAsync(stream);
                        CompleteRequestBodies++;
                        if (responseAfterSecondCompleteBody is not null && CompleteRequestBodies == 2)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseAfterSecondCompleteBody.Length}\r\nConnection: close\r\n\r\n"));
                            await stream.WriteAsync(responseAfterSecondCompleteBody);
                            return;
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        // A replay attempt may connect but must not complete a second serialized body.
                    }
                }

                inactivity.CancelAfter(TimeSpan.FromMilliseconds(750));
            }
        }
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly ImmutableArray<byte[]> responses;
        private readonly string statusLine;
        private readonly string extraHeaders;
        private readonly Task serverTask;
        private readonly List<ObservedRequest> requests = [];

        internal LoopbackServer(
            byte[] first,
            byte[]? second = null,
            string statusLine = "HTTP/1.1 200 OK",
            string extraHeaders = "")
        {
            responses = second is null ? [first] : [first, second];
            this.statusLine = statusLine;
            this.extraHeaders = extraHeaders;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal LoopbackServer(
            string first,
            string statusLine = "HTTP/1.1 200 OK",
            string extraHeaders = "")
            : this(Encoding.UTF8.GetBytes(first), statusLine: statusLine, extraHeaders: extraHeaders)
        {
        }

        internal Uri Endpoint { get; }

        internal ImmutableArray<ObservedRequest> Requests => [.. requests];

        internal Task Completion => serverTask;

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Listener shutdown is the expected cleanup path when no client connected.
            }
        }

        private async Task ServeAsync()
        {
            foreach (var response in responses)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                requests.Add(await ReadRequestAsync(stream));
                var headers = Encoding.ASCII.GetBytes(
                    $"{statusLine}\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\n{extraHeaders}Connection: close\r\n\r\n");
                await stream.WriteAsync(headers);
                await stream.WriteAsync(response);
            }
        }
    }

    private sealed class HoldingBodyServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource disposal = new();
        private readonly Task serverTask;

        internal HoldingBodyServer()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal TaskCompletionSource HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool CancellationObserved { get; private set; }

        internal Task Completion => serverTask;

        public async ValueTask DisposeAsync()
        {
            disposal.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Cancellation and listener shutdown are the expected cleanup paths.
            }

            disposal.Dispose();
        }

        private async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(disposal.Token);
            await using var stream = client.GetStream();
            _ = await ReadRequestAsync(stream);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{"u8.ToArray(), disposal.Token);
            HeadersSent.TrySetResult();
            var buffer = new byte[1];
            try
            {
                while (await stream.ReadAsync(buffer, disposal.Token) != 0)
                {
                    // Wait until the client closes its request after cancellation.
                }

                CancellationObserved = true;
            }
            catch (IOException)
            {
                CancellationObserved = true;
            }
        }
    }

    private sealed class SelfSignedTlsServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate;
        private readonly Task serverTask;

        internal SelfSignedTlsServer()
        {
            using var key = RSA.Create(2_048);
            var request = new CertificateRequest(
                "CN=127.0.0.1",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5));
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"https://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal Task Completion => serverTask;

        internal bool HttpRequestObserved { get; private set; }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Listener shutdown is the expected cleanup path when no TLS client connected.
            }

            certificate.Dispose();
        }

        private async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = certificate,
                });
                var buffer = new byte[1];
                HttpRequestObserved = await tls.ReadAsync(buffer) > 0;
            }
            catch (AuthenticationException)
            {
                // The negative TLS test expects the client to reject this self-signed certificate.
            }
            catch (IOException)
            {
                // Some platforms close the rejected TLS connection before surfacing authentication failure.
            }
        }
    }

    private static async Task<ObservedRequest> ReadRequestAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var terminator = "\r\n\r\n"u8;
        while (bytes.Count < terminator.Length
            || bytes[^4] != '\r'
            || bytes[^3] != '\n'
            || bytes[^2] != '\r'
            || bytes[^1] != '\n')
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException();
            }

            bytes.Add((byte)value);
        }

        var headers = Encoding.ASCII.GetString([.. bytes]);
        var lines = headers.Split("\r\n", StringSplitOptions.None);
        var lengthLine = Assert.Single(lines, line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var body = new byte[length];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return new ObservedRequest(lines[0], headers, body);
    }

    private sealed class NetworkEventCollector : EventListener
    {
        private readonly ConcurrentQueue<string> values = new();

        internal string Text => string.Join('\n', values);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (values is not null && eventSource.Name.StartsWith("System.Net.", StringComparison.Ordinal))
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            values.Enqueue(eventData.EventName ?? string.Empty);
            if (eventData.Payload is null)
            {
                return;
            }

            foreach (var payload in eventData.Payload)
            {
                values.Enqueue(payload?.ToString() ?? string.Empty);
            }
        }
    }

    private sealed record ObservedRequest(string RequestLine, string Headers, byte[] Body);
}
