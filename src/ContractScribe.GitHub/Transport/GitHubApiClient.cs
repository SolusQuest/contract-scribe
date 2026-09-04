using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ContractScribe.Core;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

internal sealed class GitHubApiClient : IDisposable
{
    internal const string ApiVersion = "2026-03-10";
    internal const string ProductionOrigin = "https://api.github.com/";
    internal const string UpdateRefsDocument = "mutation($input:UpdateRefsInput!){updateRefs(input:$input){clientMutationId}}";
    private readonly ValidatedGitHubPublicationAuthority authority;
    private readonly Uri origin;
    private readonly HttpMessageInvoker invoker;
    private readonly int requestTimeoutMilliseconds;
    private readonly object identityGate = new();
    private GitHubRepositoryIdentity? repository;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string? credential;
    private int disposed;

    internal ValidatedGitHubPublicationAuthority Authority => authority;

    private GitHubApiClient(ValidatedGitHubPublicationAuthority authority, string credential,
        Uri origin, HttpMessageHandler handler, int timeoutMilliseconds)
    {
        this.authority = authority;
        this.credential = credential;
        this.origin = origin;
        requestTimeoutMilliseconds = timeoutMilliseconds;
        invoker = new(handler, disposeHandler: true);
    }

    internal static GitHubApiClient Create(ValidatedGitHubPublicationAuthority authority, string credential)
    {
        Require(authority is not null && RepositoryPart(authority.RepositoryOwner)
            && RepositoryPart(authority.RepositoryName), GitHubFailureCode.InvalidRequest);
        Require(credential is { Length: > 0 and <= 8192 }
            && credential.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='),
            GitHubFailureCode.InvalidRequest);
        var test = GitHubTransportTestHook.Take(credential);
        return new(authority!, credential, test?.Endpoint ?? new Uri(ProductionOrigin),
            test?.Handler ?? CreateProductionHandler(), test?.TimeoutMilliseconds ?? 30_000);
    }

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        ActivityHeadersPropagator = null,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        MaxResponseHeadersLength = 64,
        MaxResponseDrainSize = 0,
        ResponseDrainTimeout = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    internal ValueTask<GitHubApiResult<GitHubRepository>> GetRepositoryAsync(CancellationToken cancellationToken = default) =>
        RunAsync<GitHubRepository>(() => new("repos/" + authority.RepositoryOwner + "/" + authority.RepositoryName, element =>
        {
            var value = Repository(element);
            Require(AsciiCaseEqual(value.Identity.Owner, authority.RepositoryOwner)
                && AsciiCaseEqual(value.Identity.Name, authority.RepositoryName));
            lock (identityGate)
            {
                Require(repository is null || repository == value.Identity);
                repository = value.Identity;
            }
            return value;
        }), cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubActor>> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default) =>
        RunAsync<GitHubActor>(() => new("user", element =>
        {
            var actor = Actor(element);
            Require(actor.Kind is GitHubActorKind.User or GitHubActorKind.Bot);
            return actor;
        }), cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubRef>> GetRefAsync(string fullRef, CancellationToken cancellationToken = default) =>
        RunAsync<GitHubRef>(() =>
        {
            Input(RefName(fullRef));
            var relative = string.Join('/', fullRef[5..].Split('/').Select(Uri.EscapeDataString));
            return new(RepoPath() + "/git/ref/" + relative, element => Ref(element, fullRef));
        }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubBlob>> GetBlobAsync(string oid, CancellationToken cancellationToken = default) =>
        RunAsync<GitHubBlob>(() =>
        {
            Input(IsOid(oid));
            return new(RepoPath() + "/git/blobs/" + oid, element => Blob(element, oid));
        }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubTree>> GetTreeAsync(string oid, CancellationToken cancellationToken = default) =>
        RunAsync<GitHubTree>(() =>
        {
            Input(IsOid(oid));
            return new(RepoPath() + "/git/trees/" + oid, element => Tree(element, oid));
        }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubCommit>> GetCommitAsync(string oid, CancellationToken cancellationToken = default) =>
        RunAsync<GitHubCommit>(() =>
        {
            Input(IsOid(oid));
            return new(RepoPath() + "/git/commits/" + oid, element => Commit(element, oid));
        }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubPullRequest>> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync<GitHubPullRequest>(() =>
        {
            Input(number > 0);
            var identity = Identity();
            return new(RepoPath() + "/pulls/" + number.ToString(CultureInfo.InvariantCulture), element =>
            {
                var value = PullRequest(element, identity, detail: true);
                Require(value.Number == number);
                return value;
            });
        }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubObjectIdentity>> CreateBlobAsync(string expectedOid,
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => RunAsync<GitHubObjectIdentity>(() =>
    {
        Input(IsOid(expectedOid) && bytes.Length <= MaximumBlobBytes);
        var context = new GitHubObjectContext(Identity(), authority.OperationCommitmentSha256, GitHubObjectKind.Blob, expectedOid);
        var copy = bytes.ToArray();
        var body = Encode(writer =>
        {
            writer.WriteBase64String("content", copy);
            writer.WriteString("encoding", "base64");
        });
        return new(RepoPath() + "/git/blobs", element => ObjectIdentity(element, expectedOid), body, context);
    }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubTree>> CreateTreeAsync(string expectedOid,
        ImmutableArray<GitHubTreeEntry> entries, CancellationToken cancellationToken = default) => RunAsync<GitHubTree>(() =>
    {
        Input(IsOid(expectedOid) && !entries.IsDefault && entries.Length <= MaximumTreeEntries);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        // Default JSON escaping can use six bytes per UTF-16 code unit. Bound
        // the entire encoded request before the writer allocates its buffer.
        long inputBytes = 32;
        foreach (var entry in entries)
        {
            Input(entry is not null && TreeName(entry.Path) && IsOid(entry.Oid) && paths.Add(entry.Path));
            inputBytes = checked(inputBytes + (long)entry!.Path.Length * 6 + 128);
            Input(inputBytes <= MaximumBodyBytes);
            _ = WireMode(entry.Mode);
        }
        var context = new GitHubObjectContext(Identity(), authority.OperationCommitmentSha256, GitHubObjectKind.Tree, expectedOid);
        var body = Encode(writer =>
        {
            writer.WriteStartArray("tree");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path);
                writer.WriteString("mode", WireMode(entry.Mode));
                writer.WriteString("type", ObjectType(entry.Mode));
                writer.WriteString("sha", entry.Oid);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        return new(RepoPath() + "/git/trees", element => Tree(element, expectedOid), body, context);
    }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubCommit>> CreateCommitAsync(GitHubCreateCommit request,
        CancellationToken cancellationToken = default) => RunAsync<GitHubCommit>(() =>
    {
        Input(request is not null && IsOid(request.ExpectedOid) && IsOid(request.TreeOid) && IsOid(request.ParentOid));
        InputText(request!.Message, 65536, allowEmpty: true);
        ValidateActor(request.Author);
        ValidateActor(request.Committer);
        var context = new GitHubObjectContext(Identity(), authority.OperationCommitmentSha256, GitHubObjectKind.Commit, request.ExpectedOid);
        var body = Encode(writer =>
        {
            writer.WriteString("message", request.Message);
            writer.WriteString("tree", request.TreeOid);
            writer.WriteStartArray("parents");
            writer.WriteStringValue(request.ParentOid);
            writer.WriteEndArray();
            WriteActor(writer, "author", request.Author);
            WriteActor(writer, "committer", request.Committer);
        });
        return new(RepoPath() + "/git/commits", element => Commit(element, request.ExpectedOid), body, context);
    }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubPullRequest>> CreatePullRequestAsync(GitHubCreatePullRequest request,
        CancellationToken cancellationToken = default) => RunAsync<GitHubPullRequest>(() =>
    {
        Input(request is not null && Hex(request.CreationCommitment, 64) && IsOid(request.HeadOid)
            && IsOid(request.ExpectedBaseOid) && request.ExpectedBaseOid == authority.ExpectedBaseCommitOid
            && request.HeadRef == GitHubPublicationFactory.CreateProposalRef(authority) && request.BaseRef == authority.TargetRef);
        InputText(request!.Title, 256);
        InputText(request.Body, 65536, allowEmpty: true);
        var identity = Identity();
        var context = new GitHubPullRequestContext(identity, authority.OperationCommitmentSha256,
            request.CreationCommitment, request.HeadRef, request.HeadOid, request.BaseRef, request.ExpectedBaseOid,
            Digest(request.Title), Digest(request.Body));
        var body = Encode(writer =>
        {
            writer.WriteString("title", request.Title);
            writer.WriteString("head", request.HeadRef[11..]);
            writer.WriteString("base", request.BaseRef[11..]);
            writer.WriteString("body", request.Body);
            writer.WriteBoolean("draft", true);
            writer.WriteBoolean("maintainer_can_modify", false);
        });
        return new(RepoPath() + "/pulls", element => PullRequest(element, identity, detail: true), body, context);
    }, cancellationToken);

    internal ValueTask<GitHubApiResult<GitHubAcknowledgement>> UpdateRefAsync(GitHubUpdateRef request,
        CancellationToken cancellationToken = default) => RunAsync<GitHubAcknowledgement>(() =>
    {
        Input(request is not null && IsOid(request.BeforeOid, zero: true) && IsOid(request.AfterOid)
            && request.ExpectedAbsence == (request.BeforeOid == new string('0', 40))
            && (request.Ref == GitHubPublicationFactory.CreateCoordinationRef(authority)
                || request.Ref == GitHubPublicationFactory.CreateProposalRef(authority)));
        var identity = Identity();
        var mutationId = MutationId(identity, request!);
        var context = new GitHubRefContext(identity, authority.OperationCommitmentSha256,
            request!.Ref, request.BeforeOid, request.AfterOid, mutationId);
        var body = Encode(writer =>
        {
            writer.WriteString("query", UpdateRefsDocument);
            writer.WriteStartObject("variables");
            writer.WriteStartObject("input");
            writer.WriteString("repositoryId", identity.NodeId);
            writer.WriteString("clientMutationId", mutationId);
            writer.WriteStartArray("refUpdates");
            writer.WriteStartObject();
            writer.WriteString("name", request.Ref);
            writer.WriteString("beforeOid", request.BeforeOid);
            writer.WriteString("afterOid", request.AfterOid);
            writer.WriteBoolean("force", false);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        return new("graphql", element => GraphQl(element, mutationId), body, context, Status: 200);
    }, cancellationToken);

    internal async ValueTask<GitHubApiResult<GitHubPullRequestSet>> ListPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var dispatched = false;
        try
        {
            var identity = Identity();
            var items = new Dictionary<long, GitHubPullRequest>();
            var numbers = new Dictionary<int, long>();
            var nodes = new Dictionary<string, long>(StringComparer.Ordinal);
            var page = 1;
            var observed = 0;
            long total = 0;
            int? last = null;
            DateTimeOffset? latestCreation = null;
            while (true)
            {
                var currentPage = page;
                var response = await RunAsync(() => new Prepared<PageObservation>(PullsPath(currentPage), element =>
                {
                    Require(element.ValueKind == JsonValueKind.Array && element.GetArrayLength() <= 100);
                    return new(element.EnumerateArray().Select(item => PullRequest(item, identity, detail: false)).ToImmutableArray());
                }, Page: currentPage, AdvertisedLast: last), deadline.Token).ConfigureAwait(false);
                dispatched |= response.Delivery != GitHubDelivery.NotDispatched;
                if (response.Failure is { } failure)
                {
                    var code = failure.Code == GitHubFailureCode.Cancelled && !cancellationToken.IsCancellationRequested
                        && deadline.IsCancellationRequested ? GitHubFailureCode.Timeout : failure.Code;
                    return GitHubApiResult<GitHubPullRequestSet>.Failed(code, dispatched: dispatched,
                        status: failure.HttpStatus, retry: failure.Retry, permissions: response.RequiredPermissions);
                }
                var observation = response.Value!;
                total = checked(total + observation.Bytes);
                observed = checked(observed + observation.Items.Length);
                Require(total <= MaximumCollectionBytes && observed <= MaximumPullRequests);
                Require(observation.Items.Length > 0 || observation.Next is null);
                foreach (var item in observation.Items)
                {
                    if (items.TryGetValue(item.Id, out var previous)) Require(previous == item);
                    else
                    {
                        Require(!numbers.ContainsKey(item.Number) && !nodes.ContainsKey(item.NodeId)
                            && (latestCreation is null || item.CreatedAt >= latestCreation));
                        latestCreation = item.CreatedAt;
                        items.Add(item.Id, item);
                        numbers.Add(item.Number, item.Id);
                        nodes.Add(item.NodeId, item.Id);
                    }
                }
                if (observation.Next is null)
                    return new(new(items.Values.OrderBy(item => item.Id).ToImmutableArray(), page, observed, total, true),
                        null, GitHubDelivery.Read, RequiredPermissions: response.RequiredPermissions);
                Require(page < MaximumPages);
                last = observation.Last;
                page = observation.Next.Value;
            }
        }
        catch (GitHubProtocolException exception) { return GitHubApiResult<GitHubPullRequestSet>.Failed(exception.Code, dispatched: dispatched); }
        catch (OperationCanceledException)
        {
            return GitHubApiResult<GitHubPullRequestSet>.Failed(cancellationToken.IsCancellationRequested
                ? GitHubFailureCode.Cancelled : GitHubFailureCode.Timeout, dispatched: dispatched);
        }
        catch { return GitHubApiResult<GitHubPullRequestSet>.Failed(GitHubFailureCode.HostFailure, dispatched: dispatched); }
    }

    private async ValueTask<GitHubApiResult<T>> RunAsync<T>(Func<Prepared<T>> prepare, CancellationToken cancellationToken) where T : class
    {
        Prepared<T>? prepared = null;
        var dispatched = false;
        int? status = null;
        GitHubRetryObservation? retry = null;
        GitHubPermissionAlternatives? permissions = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Input(Volatile.Read(ref disposed) == 0);
            prepared = prepare();
            var uri = new Uri(origin, prepared.Path);
            Input(uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port
                && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0);
            for (var attempt = 0; attempt < (prepared.Context is null ? 2 : 1); attempt++)
            {
                status = null;
                retry = null;
                permissions = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(requestTimeoutMilliseconds);
                using var message = new HttpRequestMessage(prepared.Body is null ? HttpMethod.Get : HttpMethod.Post, uri)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = prepared.Body is null ? null : new SingleUseContent(prepared.Body),
                };
                message.Headers.ExpectContinue = false;
                if (message.Content is not null)
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                message.Headers.UserAgent.ParseAdd("ContractScribe/0.1");
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                message.Headers.Add("X-GitHub-Api-Version", ApiVersion);
                var secret = Volatile.Read(ref credential);
                Input(secret is not null && Volatile.Read(ref disposed) == 0);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    dispatched = true;
                    using var response = await invoker.SendAsync(message, timeout.Token).ConfigureAwait(false);
                    status = (int)response.StatusCode;
                    retry = GitHubResponseHeaders.Retry(response.Headers);
                    permissions = GitHubResponseHeaders.Permissions(response.Headers);
                    Require(response.Content.Headers.ContentEncoding.Count == 0);
                    var bytes = await ReadBodyAsync(response, status == prepared.Status ? MaximumBodyBytes : 65536, timeout.Token).ConfigureAwait(false);
                    if (status != prepared.Status)
                    {
                        var code = StatusCode(status.Value, retry, bytes);
                        if (prepared.Context is null && attempt == 0 && status is 502 or 503 or 504
                            && retry.RetryAfterSeconds is null && retry.Remaining != 0)
                        {
                            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
                            continue;
                        }
                        return GitHubApiResult<T>.Failed(code, prepared.Context, dispatched, status, retry, permissions);
                    }
                    var contentType = response.Content.Headers.ContentType;
                    Require(contentType?.MediaType is "application/json" or "application/vnd.github+json"
                        && (contentType.CharSet is null || contentType.CharSet.Equals("utf-8", StringComparison.OrdinalIgnoreCase)));
                    using var document = Parse(bytes);
                    var value = prepared.Parse(document.RootElement);
                    if (prepared.Path.StartsWith("repos/", StringComparison.Ordinal)
                        && prepared.Path.Contains("/git/", StringComparison.Ordinal))
                        GitObjectUrls(document.RootElement, origin, Identity(), prepared.Path);
                    if (value is PageObservation page)
                    {
                        var pagination = GitHubResponseHeaders.Pagination(response.Headers, origin, Identity(), prepared.Page, prepared.AdvertisedLast);
                        value = (T)(object)(page with { Next = pagination.Next, Last = pagination.Last, Bytes = bytes.Length });
                    }
                    return new(value, null, prepared.Context is null ? GitHubDelivery.Read : GitHubDelivery.NeedsReadback,
                        prepared.Context, permissions);
                }
                catch (OperationCanceledException)
                {
                    var code = cancellationToken.IsCancellationRequested ? GitHubFailureCode.Cancelled
                        : timeout.IsCancellationRequested ? GitHubFailureCode.Timeout : GitHubFailureCode.ResponseLost;
                    return GitHubApiResult<T>.Failed(code, prepared.Context, dispatched, status, retry, permissions);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    if (prepared.Context is null && attempt == 0 && Transient(exception)
                        && (status is null or 502 or 503 or 504 || status == prepared.Status)
                        && retry?.RetryAfterSeconds is null && retry?.Remaining != 0)
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    return GitHubApiResult<T>.Failed(GitHubFailureCode.ResponseLost, prepared.Context, dispatched, status, retry, permissions);
                }
                finally { message.Headers.Authorization = null; }
            }
            return GitHubApiResult<T>.Failed(GitHubFailureCode.HostFailure);
        }
        catch (GitHubProtocolException exception) { return GitHubApiResult<T>.Failed(exception.Code, prepared?.Context, dispatched, status, retry, permissions); }
        catch (OperationCanceledException) { return GitHubApiResult<T>.Failed(GitHubFailureCode.Cancelled, prepared?.Context, dispatched, status, retry, permissions); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or System.Text.DecoderFallbackException
            or System.Text.EncoderFallbackException or OverflowException)
        { return GitHubApiResult<T>.Failed(GitHubFailureCode.InvalidResponse, prepared?.Context, dispatched, status, retry, permissions); }
        catch { return GitHubApiResult<T>.Failed(GitHubFailureCode.HostFailure, prepared?.Context, dispatched, status, retry, permissions); }
    }

    private static bool Transient(Exception exception) => exception is IOException
        || exception is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded };

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength;
        Require(declared is null || declared >= 0 && declared <= maximum);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var writer = new ArrayBufferWriter<byte>();
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = maximum - writer.WrittenCount;
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            Require(count <= remaining);
            writer.Write(buffer.AsSpan(0, count));
        }
        Require(declared is null || declared == writer.WrittenCount);
        return writer.WrittenSpan.ToArray();
    }

    private static GitHubFailureCode StatusCode(int status, GitHubRetryObservation retry, byte[] body)
    {
        var rate = status == 429 || status is 403 or 422 && (retry.Remaining == 0 || retry.RetryAfterSeconds is not null);
        if (!rate && status is 403 or 422 && body.Length > 0)
        {
            try
            {
                using var document = Parse(body);
                var message = String(document.RootElement, "message", 8192, allowEmpty: true);
                rate = message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("abuse", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is JsonException or GitHubProtocolException or System.Text.DecoderFallbackException) { }
        }
        if (rate) return GitHubFailureCode.RateLimit;
        return status switch
        {
            401 => GitHubFailureCode.Authentication,
            403 => GitHubFailureCode.Permission,
            404 => GitHubFailureCode.NotFound,
            409 => GitHubFailureCode.Conflict,
            400 or 422 => GitHubFailureCode.Validation,
            >= 200 and < 400 => GitHubFailureCode.InvalidResponse,
            _ => GitHubFailureCode.HostFailure,
        };
    }

    private GitHubRepositoryIdentity Identity()
    {
        lock (identityGate)
        {
            Input(repository is not null);
            return repository!;
        }
    }
    private string RepoPath()
    {
        var identity = Identity();
        return "repos/" + identity.Owner + "/" + identity.Name;
    }
    private string PullsPath(int page) => RepoPath() + "/pulls?state=all&sort=created&direction=asc&per_page=100&page="
        + page.ToString(CultureInfo.InvariantCulture);
    private static GitHubObjectIdentity ObjectIdentity(JsonElement element, string expected)
    {
        Require(Oid(element, "sha") == expected);
        return new(expected);
    }
    private static bool AsciiCaseEqual(string left, string right) => left.All(char.IsAscii) && right.All(char.IsAscii)
        && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static void Input(bool condition) => Require(condition, GitHubFailureCode.InvalidRequest);
    private static void InputText(string? text, int maximum, bool allowEmpty = false) =>
        Input(text is not null && text.Length <= maximum && (allowEmpty || text.Length > 0) && ValidUtf16(text));
    private static void ValidateActor(GitHubCommitActor actor)
    {
        Input(actor is not null);
        InputText(actor!.Name, 256);
        InputText(actor.Email, 320);
        Input(!actor.Name.Any(char.IsControl) && !actor.Email.Any(char.IsControl)
            && actor.Date.Ticks % TimeSpan.TicksPerSecond == 0);
    }
    private static void WriteActor(Utf8JsonWriter writer, string name, GitHubCommitActor actor)
    {
        writer.WriteStartObject(name);
        writer.WriteString("name", actor.Name);
        writer.WriteString("email", actor.Email);
        writer.WriteString("date", actor.Date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        Input(buffer.WrittenCount <= MaximumBodyBytes);
        return buffer.WrittenSpan.ToArray();
    }
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
    private string MutationId(GitHubRepositoryIdentity identity, GitHubUpdateRef update)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in new[] { "contract-scribe/github-ref-transport/v1", identity.Id.ToString(CultureInfo.InvariantCulture),
            identity.NodeId, identity.Owner, identity.Name, authority.OperationCommitmentSha256, update.Ref, update.BeforeOid, update.AfterOid })
        {
            var bytes = Utf8.GetBytes(field);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public override string ToString() => nameof(GitHubApiClient);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref credential, null);
        try { invoker.Dispose(); } catch { /* Teardown cannot export handler exception text. */ }
    }

    private sealed record Prepared<T>(string Path, Func<JsonElement, T> Parse, byte[]? Body = null,
        GitHubMutationContext? Context = null, int Status = 0, int Page = 0, int? AdvertisedLast = null) where T : class
    {
        internal int Status { get; init; } = Status != 0 ? Status : Body is null ? 200 : 201;
    }
    private sealed record PageObservation(ImmutableArray<GitHubPullRequest> Items, int? Next = null, int? Last = null, int Bytes = 0);
    private sealed class SingleUseContent(byte[] bytes) : HttpContent
    {
        private int serializations;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref serializations) != 1) throw new GitHubProtocolException(GitHubFailureCode.ResponseLost);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    }
}
