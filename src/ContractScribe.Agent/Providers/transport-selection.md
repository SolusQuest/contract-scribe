# M3 minimum provider transport selection

Issue [#107](https://github.com/SolusQuest/contract-scribe/issues/107) selects one transport mechanism for the already accepted common OpenAI-compatible, non-streaming message/tool/result/terminal direction. This record does not claim compatibility with a provider or model. Live compatibility remains owned by the later executable evaluation corpus.

## Decision

Select direct .NET BCL HTTP and JSON for the first transport:

- `SocketsHttpHandler` and `HttpMessageInvoker` own the confidential network boundary;
- `System.Text.Json` owns the selected deterministic request and bounded response codec;
- ContractScribe retains ordered-message, closed-tool, terminal, retry, usage/cache, and failure authority;
- no NuGet package, provider hierarchy, fallback, or alternate wire path is added.

The repository targets `net10.0`; its minimum SDK 10.0.102 contains runtime 10.0.2. The BCL behavior was therefore inspected first at [.NET runtime v10.0.2](https://github.com/dotnet/runtime/releases/tag/v10.0.2), then compared with [v10.0.10](https://github.com/dotnet/runtime/releases/tag/v10.0.10). The exact source blobs used by this decision are identical at both tags:

| Source | Blob SHA |
| --- | --- |
| `SocketsHttpHandler.cs` | `4d4fccd7033d2b4cdc5bd1bcf906c389d00e6cfe` |
| `HttpConnectionSettings.cs` | `b4006543504d701f4e49172c43f138ea9a649aba` |
| `HttpConnectionPool.cs` | `e356c0306aba42ed39fe1e67969b8fe15d1a783a` |
| `DiagnosticsHandler.cs` | `79fc283aeb421b13fefa91f0c5943a1179bb12ae` |
| `HttpConnection.cs` | `0bf9d07e9045270649e9737bebbf2cb9fd02a032` |
| `RequestRetryType.cs` | `437f20c15926e3360982b32ab6351230b5a521e2` |

The handler insertion, retry, version, drain, and exception paths relied on here are consequently materially identical at the guaranteed minimum and the currently installed servicing runtime. The implementation pins HTTP/1.1 exact, disables redirects, automatic decompression, proxy, cookies, response draining, and distributed trace propagation, and uses a single-serialization body plus bounded response streaming. The primary API references are [`SocketsHttpHandler`](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler?view=net-10.0), [`ActivityHeadersPropagator`](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.activityheaderspropagator?view=net-10.0), and [`HttpVersionPolicy`](https://learn.microsoft.com/dotnet/api/system.net.http.httpversionpolicy?view=net-10.0).

## Rejected concrete candidates

### OpenAI .NET 2.12.0

The inspected official package is [`OpenAI` 2.12.0](https://www.nuget.org/packages/OpenAI/2.12.0), release tag [`OpenAI_2.12.0`](https://github.com/openai/openai-dotnet/releases/tag/OpenAI_2.12.0), commit `6450c84`. Its `ChatClient`, chat/tool models, `OpenAIClientOptions.Endpoint`, generated protocol surface, and `System.ClientModel` pipeline provide a convenient OpenAI client. They also add a generated protocol and client pipeline where #107 needs one narrow selected shape, exact raw-field visibility, product-owned failure/retry classification, and diagnostic suppression. Protocol methods can recover raw visibility, but then the package provides too little benefit to justify its dependency and pipeline boundary for this issue.

This is not a rejection of the package's quality or future use. It is the minimum-dependency choice for the current single shape.

### Microsoft.Extensions.AI 10.8.3

The inspected framework packages are [`Microsoft.Extensions.AI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.8.3) and [`Microsoft.Extensions.AI.OpenAI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.8.3). `IChatClient` and `ChatClientBuilder` offer general chat/tool abstractions. Function invocation, caching, and OpenTelemetry are optional middleware, as shown by the official [`IChatClient` pipeline documentation](https://learn.microsoft.com/dotnet/ai/ichatclient); they are not treated as intrinsic defaults in this comparison.

ContractScribe already owns the exchange interface, ordered product messages, closed tool loop, terminal contract, retry boundary, cache semantics, and telemetry restrictions. Adding another abstraction plus a concrete provider client would duplicate those authorities and still require a product-specific raw normalization boundary. It is therefore larger than the direct BCL path for #107.

## Exact selected request subset

The selected root property order is `model`, `messages`, `tools`, `tool_choice`, `parallel_tool_calls`, `max_tokens`, `stream`, and `n`. `tool_choice` is `required`, parallel calls are enabled, streaming is disabled, and `n` is one.

The five product blocks stay separate and ordered:

| Product message | Wire role |
| --- | --- |
| `SystemPolicy` | `system` |
| `RepositoryInstructions` | `user` |
| `MaintainedContext` | `user` |
| `RunPolicy` | `system` |
| `TargetEvidence` | `user` |

Ordinary product operations sort by ordinal product ID and map request-locally to `cs_tool_000` through `cs_tool_126`. The terminal maps to `cs_terminal` and remains last. The aliases never cross back into product values. Prior rounds reconstruct from each contiguous `ResponseIndex` sequence beginning at zero; each round emits one assistant tool-call message and its ordered tool-result messages. Tool-result content is the canonical object `{ "outcome": ..., "result": ..., "evidenceReferences": [...] }` encoded once as the protocol string. Product-owned trusted evidence stays separate from opaque tool result JSON, and the deterministic product projection commits to the same ordered rows.

The full JSON body vector contains a fixed synthetic model and the exact RunPolicy content, including artifact identity, attempt identity/number, context, style, and limits. The separately hashed product-correctness projection excludes model, endpoint, credential, HTTP headers, aliases, timestamps, transport-generated request IDs, and the separately held provider-request counter. Changing caller configuration therefore cannot change product correctness.

Every network request is `POST` to the exact configured endpoint over exact HTTP/1.1. It emits `Content-Type: application/json; charset=utf-8`, `Accept: application/json`, optional caller-owned bearer authorization, no custom User-Agent, and no trace headers. BCL-generated Host and Content-Length are transport facts outside the product projection. The terminal function uses fixed description `Submit one structured terminal result.`.

## Closed capability and security subset

- At most 127 ordinary tools plus the terminal; schemas must be duplicate-free JSON objects.
- The endpoint is the exact POST target. Query, fragment, and userinfo are rejected.
- Authenticated execution is HTTPS with platform certificate validation and no redirect.
- Plain HTTP is limited to credential-free canonical literal `127.0.0.1` or `[::1]` with an explicit port. Bearer credentials use the RFC 6750 `b64token` alphabet, require at least one non-padding character, and permit `=` only as terminal padding.
- Only an unencoded status-200 JSON response enters the bounded parser. Non-success status takes precedence over body prose. Non-streaming tool calls are ordered by their array position and may not carry a streaming-only `index` field.
- A provider-owned inner handler converts nonfatal transport exceptions to stable content-free exceptions before `HttpMessageInvoker` telemetry can observe them; the sanitized exception text omits raw inner exceptions and stack paths. Connection EOF before any response is transient, while premature EOF after a status-200 response is malformed and not retryable.
- Provider error bodies, raw responses, credentials, request bodies, and exception prose do not become product values or diagnostics. Incoming ordinary and terminal call IDs share the same bounded correlation domain and are checked against both the current response and all completed rounds before a tool can run.
- Retry, delay, deadline, fallback, pricing, and cache correctness remain outside the transport.

Redirect following, compressed responses, HTTP/2 or HTTP/3, proxies, HTTP-date retry hints, custom certificate policy, request compression, provider-specific branches, and support claims are unsupported in this issue.

## Deterministic selection probe

The focused `DocumentationScribeProviderTransportTests.Selection_probe_preserves_the_complete_product_path` test is the executable decision gate. It exercises five messages, two tools plus terminal, a first tool call, its canonical result, a terminal response, and direct usage/cache observations through the pure selected codec before the socket-owning exchange is added. The test also verifies a stable full-wire digest and a caller-isolated product-projection digest.

The probe first passed with:

```text
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~DocumentationScribeProviderTransportTests
```

- full fixed-input wire SHA-256: `1b7104759b372c99e31178b4f2381cfe98a280410808c4fe8a5af24b85ca1761`;
- caller-isolated product projection SHA-256: `d97a87532f0c1776bd07324fbeeadfd146d4968b9c0e81b1c0dfdf4e1edcf0d8`.

The vector retained every required field without a provider branch, hidden field, or unstable ordering, so the direct BCL selection passed its implementation gate.

The same selection and production-handler policy tests were then invoked from an uncommitted test launcher with `RuntimeFrameworkVersion=10.0.2` and roll-forward disabled:

```text
dotnet run --project .codex/tmp/issue-107/min-runtime-probe/MinimumRuntimeProbe.csproj --configuration Release --no-restore
minimum-runtime-probe passed on 10.0.2
```

The launcher is local validation infrastructure rather than a product or test project; its only purpose is to prove the exact checked tests on the repository's guaranteed minimum runtime without changing `global.json`.
