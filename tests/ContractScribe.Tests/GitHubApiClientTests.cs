using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

[CollectionDefinition("GitHub transport hook", DisableParallelization = true)]
public sealed class GitHubTransportCollection;

[Collection("GitHub transport hook")]
public sealed class GitHubApiClientTests
{
    private const string Origin = "http://127.0.0.1:18765/";
    private static string Oid(char c) => new(c, 40);
    private static string Hash(char c) => new(c, 64);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Every_mutation_has_exact_closed_wire_shape_and_requires_owner_readback(int family)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = Success;
        var result = await Mutate(harness, family);
        Assert.Null(result.Failure);
        Assert.Equal(GitHubDelivery.NeedsReadback, result.Delivery);
        Assert.NotNull(result.Context);
        Assert.Equal(harness.Authority.OperationCommitmentSha256, result.Context.OperationCommitment);
        Assert.Equal(new(42, "R_42", "Owner", "repo"), result.Context.Repository);
        var sent = Assert.Single(harness.Handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(HttpVersion.Version11, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, sent.VersionPolicy);
        Assert.False(sent.ExpectContinue);
        Assert.True(sent.AuthorizationPresent);
        Assert.Equal(GitHubApiClient.ApiVersion, sent.ApiVersion);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        switch (family)
        {
            case 0:
                Assert.Equal("/repos/Owner/repo/git/blobs", sent.Path);
                Keys(root, "content", "encoding");
                Assert.Equal("base64", root.GetProperty("encoding").GetString());
                Assert.Equal("private candidate bytes", Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("content").GetString()!)));
                Assert.Equal(GitHubObjectKind.Blob, Assert.IsType<GitHubObjectContext>(result.Context).Kind);
                break;
            case 1:
                Assert.Equal("/repos/Owner/repo/git/trees", sent.Path);
                Keys(root, "tree");
                var entry = Assert.Single(root.GetProperty("tree").EnumerateArray());
                Keys(entry, "path", "mode", "type", "sha");
                Assert.Equal("100644", entry.GetProperty("mode").GetString());
                Assert.Equal("blob", entry.GetProperty("type").GetString());
                Assert.Equal(GitHubObjectKind.Tree, Assert.IsType<GitHubObjectContext>(result.Context).Kind);
                break;
            case 2:
                Assert.Equal("/repos/Owner/repo/git/commits", sent.Path);
                Keys(root, "message", "tree", "parents", "author", "committer");
                Assert.Equal(Oid('1'), Assert.Single(root.GetProperty("parents").EnumerateArray()).GetString());
                Keys(root.GetProperty("author"), "name", "email", "date");
                Assert.Equal(GitHubObjectKind.Commit, Assert.IsType<GitHubObjectContext>(result.Context).Kind);
                break;
            case 3:
                Assert.Equal("/repos/Owner/repo/pulls", sent.Path);
                Keys(root, "title", "body", "head", "base", "draft", "maintainer_can_modify");
                Assert.True(root.GetProperty("draft").GetBoolean());
                Assert.False(root.GetProperty("maintainer_can_modify").GetBoolean());
                var pr = Assert.IsType<GitHubPullRequestContext>(result.Context);
                Assert.Equal(Hash('a'), pr.CreationCommitment);
                Assert.Equal(Oid('2'), pr.HeadOid);
                Assert.Equal(Oid('1'), pr.ExpectedBaseOid);
                Assert.Equal("refs/heads/main", pr.BaseRef);
                Assert.Equal(GitHubPublicationFactory.CreateProposalRef(harness.Authority), pr.HeadRef);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("private title"u8)), pr.TitleSha256);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("private body"u8)), pr.BodySha256);
                Assert.True(pr.Draft);
                Assert.False(pr.MaintainerCanModify);
                break;
            case 4:
                Assert.Equal("/graphql", sent.Path);
                Keys(root, "query", "variables");
                Assert.Equal(GitHubApiClient.UpdateRefsDocument, root.GetProperty("query").GetString());
                Keys(root.GetProperty("variables"), "input");
                var input = root.GetProperty("variables").GetProperty("input");
                Keys(input, "repositoryId", "clientMutationId", "refUpdates");
                Assert.Equal("R_42", input.GetProperty("repositoryId").GetString());
                var update = Assert.Single(input.GetProperty("refUpdates").EnumerateArray());
                Keys(update, "name", "beforeOid", "afterOid", "force");
                Assert.False(update.GetProperty("force").GetBoolean());
                var context = Assert.IsType<GitHubRefContext>(result.Context);
                Assert.Equal(Oid('0'), context.BeforeOid);
                Assert.Equal(Oid('2'), context.AfterOid);
                Assert.Equal(context.ClientMutationId, input.GetProperty("clientMutationId").GetString());
                Assert.Equal(context.Ref, update.GetProperty("name").GetString());
                break;
        }
        if (result.Context is GitHubObjectContext obj) Assert.Equal(Oid('2'), obj.ExpectedOid);
        Assert.DoesNotContain("private", result.Context.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Every_mutation_preserves_same_recovery_identity_across_all_uncertain_windows(int family)
    {
        GitHubMutationContext? expected = null;
        foreach (var window in new[] { "ack", "loss", "body-loss", "timeout", "cancel", "malformed", "replay", "403", "redirect" })
        {
            using var cancellation = new CancellationTokenSource();
            using var harness = await Harness.Create(timeout: window == "timeout" ? 50 : 30_000);
            var serializations = 0;
            harness.Handler.Reply = async (request, token) =>
            {
                if (window == "replay")
                {
                    using var first = new MemoryStream();
                    await request.Content!.CopyToAsync(first, token);
                    serializations++;
                    using var second = new MemoryStream();
                    await request.Content.CopyToAsync(second, token);
                    serializations++;
                    return Json("{}");
                }
                await CaptureBody(request, token);
                if (window == "loss") throw new HttpRequestException("private token and response", new IOException("secret"));
                if (window == "body-loss") return new(HttpStatusCode.Created) { Content = new StreamContent(new FaultStream()) };
                if (window == "timeout") await Task.Delay(Timeout.Infinite, token);
                if (window == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (window == "malformed") return Json("{ invalid private response", family == 4 ? 200 : 201);
                if (window == "403") return Json("{\"message\":\"private denied\"}", 403);
                if (window == "redirect")
                {
                    var redirect = Json("{}", 307);
                    redirect.Headers.Location = new Uri("https://hostile.invalid/secret");
                    return redirect;
                }
                return await SuccessFromCaptured(request);
            };
            var result = await Mutate(harness, family, cancellation.Token);
            Assert.Single(harness.Handler.Requests);
            Assert.NotNull(result.Context);
            expected ??= result.Context;
            Assert.Equal(expected, result.Context);
            Assert.Equal(window == "ack" ? GitHubDelivery.NeedsReadback : GitHubDelivery.Ambiguous, result.Delivery);
            if (window != "ack")
            {
                Assert.NotNull(result.Failure);
                Assert.Null(result.Value);
                Assert.DoesNotContain("private", result.Failure.ToString());
                Assert.DoesNotContain("secret", result.Failure.ToString());
            }
            if (window == "replay") Assert.Equal(1, serializations);
            if (window == "timeout") Assert.Equal(GitHubFailureCode.Timeout, result.Failure!.Code);
            if (window == "cancel") Assert.Equal(GitHubFailureCode.Cancelled, result.Failure!.Code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Predispatch_cancellation_never_sends_or_claims_ambiguity(int family)
    {
        using var harness = await Harness.Create();
        var result = await Mutate(harness, family, new CancellationToken(true));
        Assert.Equal(GitHubFailureCode.Cancelled, result.Failure!.Code);
        Assert.Equal(GitHubDelivery.NotDispatched, result.Delivery);
        Assert.Null(result.Context);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Reads_cover_exact_endpoints_and_ignore_only_unconsumed_additive_metadata()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/user" => Json(Actor()),
            var path when path.Contains("/git/ref/", StringComparison.Ordinal) => Json(new JsonObject
            { ["ref"] = "refs/heads/a/%2F/文", ["node_id"] = "REF_1", ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = Oid('2') } }),
            var path when path.Contains("/git/blobs/", StringComparison.Ordinal) => Json(new JsonObject
            { ["sha"] = Oid('2'), ["encoding"] = "base64", ["size"] = 3, ["content"] = "YWJj\n", ["extra"] = new JsonObject { ["unused"] = true } }),
            var path when path.Contains("/git/trees/", StringComparison.Ordinal) => Json(Tree()),
            var path when path.Contains("/git/commits/", StringComparison.Ordinal) => Json(Commit()),
            _ => Json(Pull()),
        });
        Assert.Equal(GitHubActorKind.User, (await harness.Client.GetAuthenticatedUserAsync()).Value!.Kind);
        Assert.Equal("refs/heads/a/%2F/文", (await harness.Client.GetRefAsync("refs/heads/a/%2F/文")).Value!.Name);
        Assert.Equal("abc"u8.ToArray(), (await harness.Client.GetBlobAsync(Oid('2'))).Value!.Bytes.ToArray());
        Assert.Single((await harness.Client.GetTreeAsync(Oid('2'))).Value!.Entries);
        Assert.Single((await harness.Client.GetCommitAsync(Oid('2'))).Value!.Parents);
        Assert.Equal(1, (await harness.Client.GetPullRequestAsync(1)).Value!.Number);
        Assert.Equal(new[] { "/user", "/repos/Owner/repo/git/ref/heads/a/%252F/%E6%96%87",
            "/repos/Owner/repo/git/blobs/" + Oid('2'), "/repos/Owner/repo/git/trees/" + Oid('2'),
            "/repos/Owner/repo/git/commits/" + Oid('2'), "/repos/Owner/repo/pulls/1" }, harness.Handler.Requests.Select(item => item.Path));
        Assert.All(harness.Handler.Requests, item => Assert.Equal("GET", item.Method));
    }

    [Fact]
    public async Task Repository_allows_initial_ascii_case_alias_then_pins_all_identity_components()
    {
        using var harness = await Harness.Create(owner: "owner", name: "REPO");
        foreach (var field in new[] { "id", "node_id", "owner", "name", "full_name" })
        {
            var changed = Repository();
            switch (field)
            {
                case "id": changed[field] = 43; break;
                case "node_id": changed[field] = "R_43"; break;
                case "owner": changed["owner"]!["login"] = "owner"; changed["full_name"] = "owner/repo"; break;
                case "name": changed[field] = "REPO"; changed["full_name"] = "Owner/REPO"; break;
                default: changed[field] = "elsewhere/repo"; break;
            }
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(changed));
            Assert.Equal(GitHubFailureCode.InvalidResponse, (await harness.Client.GetRepositoryAsync()).Failure!.Code);
        }
    }

    [Theory]
    [InlineData("https://evil.invalid/")]
    [InlineData("http://localhost:18765/")]
    [InlineData("http://127.1:18765/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:18765/path")]
    [InlineData("http://user@127.0.0.1:18765/")]
    [InlineData("http://127.0.0.1:18765/?x=y")]
    [InlineData("http://127.0.0.1:18765/#fragment")]
    [InlineData("http://127.0.0.2:18765/")]
    public void Hook_rejects_every_noncanonical_or_nonlocal_endpoint(string endpoint)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Register(new Uri(endpoint), new ScriptedHandler()));
        Assert.IsType<GitHubProtocolException>(exception.InnerException);
    }

    [Fact]
    public void Hook_is_inert_single_use_and_cannot_send_any_real_credential()
    {
        var authority = Authority();
        Assert.Throws<GitHubProtocolException>(() => GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder));
        using (var registration = Register(new Uri(Origin), new ScriptedHandler()))
        {
            Assert.Throws<GitHubProtocolException>(() => GitHubApiClient.Create(authority, "opaque-real-shaped-token"));
            Assert.Throws<TargetInvocationException>(() => Register(new Uri(Origin), new ScriptedHandler()));
            using var client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
            Assert.Throws<GitHubProtocolException>(() => GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder));
            Assert.Equal(nameof(GitHubApiClient), client.ToString());
        }
        using var ipv6 = Register(new Uri("http://[::1]:18765/"), new ScriptedHandler());
        using var ipv6Client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder);
    }

    [Theory]
    [InlineData("refs/heads/../main")]
    [InlineData("refs/heads/a?token=x")]
    [InlineData("refs/heads/a\\b")]
    [InlineData("refs/heads/a.lock")]
    [InlineData("refs/heads/a.lock/branch")]
    [InlineData("refs/heads/branch.")]
    [InlineData("refs/heads/a b")]
    [InlineData("refs/heads/a\u007fb")]
    [InlineData("refs/heads/a\u001fb")]
    [InlineData("refs/heads/a//b")]
    [InlineData("refs/tags/v1")]
    [InlineData("https://evil.invalid/")]
    public async Task Invalid_refs_never_reach_a_handler(string reference)
    {
        using var harness = await Harness.Create();
        Assert.Equal(GitHubFailureCode.InvalidRequest, (await harness.Client.GetRefAsync(reference)).Failure!.Code);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Invalid_mutation_inputs_are_rejected_before_dispatch()
    {
        using var harness = await Harness.Create();
        var failures = new List<GitHubFailure?>
        {
            (await harness.Client.CreateBlobAsync(Oid('0'), ReadOnlyMemory<byte>.Empty)).Failure,
            (await harness.Client.CreateTreeAsync(Oid('2'), [new("../escape", GitHubTreeMode.File, Oid('2'), null)])).Failure,
            (await harness.Client.CreateTreeAsync(Oid('2'), [new("a", GitHubTreeMode.File, Oid('2'), null), new("a", GitHubTreeMode.File, Oid('2'), null)])).Failure,
            (await harness.Client.CreateTreeAsync(Oid('2'), default)).Failure,
            (await harness.Client.CreateCommitAsync(CommitRequest() with { ParentOid = Oid('0') })).Failure,
            (await harness.Client.CreatePullRequestAsync(PullRequestInput(harness.Authority) with { ExpectedBaseOid = Oid('3') })).Failure,
            (await harness.Client.UpdateRefAsync(new("refs/heads/main", Oid('1'), Oid('2'), false))).Failure,
            (await harness.Client.UpdateRefAsync(Update(harness.Authority) with { AfterOid = Oid('0') })).Failure,
            (await harness.Client.UpdateRefAsync(Update(harness.Authority) with { ExpectedAbsence = false })).Failure,
            (await harness.Client.UpdateRefAsync(Update(harness.Authority) with { BeforeOid = Oid('1') })).Failure,
        };
        Assert.All(failures, failure => Assert.Equal(GitHubFailureCode.InvalidRequest, failure!.Code));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Deterministic_mutation_id_changes_with_each_bound_identity()
    {
        var ids = new HashSet<string>();
        foreach (var variant in Enumerable.Range(0, 7))
        {
            using var harness = await Harness.Create(operation: variant == 6 ? "operation-2" : "operation-1",
                repositoryId: variant == 4 ? 43 : 42, repositoryNode: variant == 5 ? "R_43" : "R_42");
            harness.Handler.Reply = Success;
            var update = Update(harness.Authority);
            update = variant switch
            {
                1 => update with { Ref = GitHubPublicationFactory.CreateProposalRef(harness.Authority) },
                2 => update with { BeforeOid = Oid('3'), ExpectedAbsence = false },
                3 => update with { AfterOid = Oid('4') },
                _ => update,
            };
            var first = await harness.Client.UpdateRefAsync(update);
            var repeat = await harness.Client.UpdateRefAsync(update);
            Assert.Null(first.Failure);
            var id = Assert.IsType<GitHubRefContext>(first.Context).ClientMutationId;
            Assert.Equal(id, Assert.IsType<GitHubRefContext>(repeat.Context).ClientMutationId);
            Assert.True(ids.Add(id));
        }
    }

    [Theory]
    [InlineData(401, "Authentication")]
    [InlineData(403, "Permission")]
    [InlineData(404, "NotFound")]
    [InlineData(409, "Conflict")]
    [InlineData(422, "Validation")]
    [InlineData(429, "RateLimit")]
    [InlineData(500, "HostFailure")]
    [InlineData(302, "InvalidResponse")]
    [InlineData(201, "InvalidResponse")]
    public async Task Status_failures_are_closed_and_not_retried(int status, string expected)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) => Task.FromResult(Json("{\"message\":\"private error\"}", status));
        var result = await harness.Client.GetAuthenticatedUserAsync();
        Assert.Equal(expected, result.Failure!.Code.ToString());
        Assert.Equal(status, result.Failure.HttpStatus);
        Assert.Single(harness.Handler.Requests);
        Assert.Null(result.Value);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Transient_reads_retry_once_but_rate_hints_prevent_retry(int status)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) => Task.FromResult(Json("{}", status));
        Assert.NotNull((await harness.Client.GetAuthenticatedUserAsync()).Failure);
        Assert.Equal(2, harness.Handler.Requests.Count);
        harness.Handler.Requests.Clear();
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json("{}", status);
            response.Headers.Add("Retry-After", "2");
            return Task.FromResult(response);
        };
        var observed = await harness.Client.GetAuthenticatedUserAsync();
        Assert.Equal(2, observed.Failure!.Retry!.RetryAfterSeconds);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task Transport_read_retry_excludes_permanent_tls_and_protocol_failures()
    {
        using var harness = await Harness.Create();
        foreach (var error in new[] { HttpRequestError.ConnectionError, HttpRequestError.ResponseEnded,
            HttpRequestError.SecureConnectionError, HttpRequestError.InvalidResponse, HttpRequestError.UserAuthenticationError })
        {
            harness.Handler.Requests.Clear();
            harness.Handler.Reply = (_, _) => throw new HttpRequestException(error, "private transport text");
            Assert.Equal(GitHubFailureCode.ResponseLost, (await harness.Client.GetAuthenticatedUserAsync()).Failure!.Code);
            Assert.Equal(error is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded ? 2 : 1, harness.Handler.Requests.Count);
        }
    }

    [Fact]
    public async Task Permission_alternatives_preserve_or_of_and_and_never_invent_granted_scopes()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json("{}", 403);
            response.Headers.Add("X-Accepted-GitHub-Permissions", "contents=write,metadata=read;issues=write;pull_requests=write");
            return Task.FromResult(response);
        };
        var result = await harness.Client.GetAuthenticatedUserAsync();
        Assert.Equal(GitHubFailureCode.Permission, result.Failure!.Code);
        Assert.True(result.RequiredPermissions!.HasUnrepresentedAlternatives);
        Assert.Equal(2, result.RequiredPermissions.Alternatives.Length);
        Assert.Equal(new[] { GitHubPermission.Metadata, GitHubPermission.Contents }, result.RequiredPermissions.Alternatives[0].Select(item => item.Permission));
        Assert.Equal(GitHubPermission.PullRequests, Assert.Single(result.RequiredPermissions.Alternatives[1]).Permission);
        foreach (var invalid in new[] { "contents=admin", "contents=read,contents=write", "contents=read;", "issues=admin", "=read", "contents=read=write" })
        {
            using var response = Json("{}");
            response.Headers.TryAddWithoutValidation("X-Accepted-GitHub-Permissions", invalid);
            Assert.Throws<GitHubProtocolException>(() => GitHubResponseHeaders.Permissions(response.Headers));
        }
    }

    [Fact]
    public async Task Rate_headers_and_secondary_limit_body_are_sanitized_observations()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json("{\"message\":\"private secondary rate limit detail\"}", 403);
            response.Headers.Add("Date", "Thu, 03 Sep 2026 08:00:00 GMT");
            response.Headers.Add("Retry-After", "Thu, 03 Sep 2026 08:00:05 GMT");
            response.Headers.Add("X-RateLimit-Reset", "1788422405");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        };
        var result = await harness.Client.GetAuthenticatedUserAsync();
        Assert.Equal(GitHubFailureCode.RateLimit, result.Failure!.Code);
        Assert.Equal(new(5, 1788422405, 0), result.Failure.Retry);
        Assert.Single(harness.Handler.Requests);
        foreach (var invalid in new[] { "-1", "86401", "nonsense" })
        {
            using var response = Json("{}");
            response.Headers.TryAddWithoutValidation("Retry-After", invalid);
            Assert.Throws<GitHubProtocolException>(() => GitHubResponseHeaders.Retry(response.Headers));
        }
    }

    [Fact]
    public async Task Pagination_accepts_numeric_alias_rebuilds_canonical_path_and_deduplicates_identical_rows()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (request, _) =>
        {
            var first = request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal);
            var response = Json(new JsonArray(Pull(1), first ? null : Pull(2)));
            if (first)
            {
                response = Json(new JsonArray(Pull(1)));
                response.Headers.Add("Link", Link("/repositories/42/pulls", 2, "next"));
            }
            return Task.FromResult(response);
        };
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Null(result.Failure);
        Assert.True(result.Value!.Exhausted);
        Assert.Equal(2, result.Value.Pages);
        Assert.Equal(3, result.Value.ObservedItems);
        Assert.Equal(2, result.Value.Items.Length);
        Assert.True(result.Value.BodyBytes > 0);
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.All(harness.Handler.Requests, request => Assert.StartsWith("/repos/Owner/repo/pulls?", request.Path));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("deleted")]
    [InlineData("missing-ref")]
    public async Task Ordinary_foreign_or_deleted_heads_remain_typed_observations_for_owner(string kind)
    {
        using var harness = await Harness.Create();
        var pull = Pull();
        if (kind == "foreign") pull["head"]!["repo"] = Repository("Other", "fork", 99, "R_99");
        else pull["head"]!["repo"] = null;
        if (kind == "missing-ref") { pull["head"]!["ref"] = null; pull["head"]!["sha"] = null; }
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonArray(pull.DeepClone())));
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Null(result.Failure);
        var observed = Assert.Single(result.Value!.Items);
        if (kind == "foreign") Assert.Equal(99, observed.Head.Repository!.Id);
        else Assert.Null(observed.Head.Repository);
        if (kind == "missing-ref") { Assert.Null(observed.Head.Ref); Assert.Null(observed.Head.Oid); }
    }

    [Theory]
    [InlineData("https://hostile.invalid/repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=2")]
    [InlineData("http://127.0.0.1:18765/repositories/43/pulls?state=all&sort=created&direction=asc&per_page=100&page=2")]
    [InlineData("http://127.0.0.1:18765/repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=1")]
    [InlineData("http://127.0.0.1:18765/repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=3")]
    [InlineData("http://127.0.0.1:18765/repos/Owner/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2")]
    [InlineData("http://127.0.0.1:18765/repos/Owner/repo/pulls?state=all&sort=created&direction=asc&per_page=100&page=2&page=2")]
    public async Task Hostile_or_incomplete_pagination_never_sends_advertised_uri(string url)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json(new JsonArray(Pull()));
            response.Headers.Add("Link", "<" + url + ">; rel=\"next\"");
            return Task.FromResult(response);
        };
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code);
        Assert.Null(result.Value);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("node_id")]
    [InlineData("body")]
    [InlineData("id")]
    [InlineData("base")]
    [InlineData("terminal")]
    [InlineData("empty-next")]
    public async Task Contradictory_pages_fail_all_or_nothing(string change)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (request, _) =>
        {
            var first = request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal);
            var pull = Pull();
            if (!first)
            {
                if (change == "number") pull["number"] = 2;
                if (change == "node_id") pull["node_id"] = "PR_other";
                if (change == "body") pull["body"] = "changed";
                if (change == "id") pull["id"] = 999;
                if (change == "base") pull["base"]!["repo"] = Repository("Other", "repo");
            }
            var response = Json(change == "empty-next" ? new JsonArray() : new JsonArray(pull));
            if (first) response.Headers.Add("Link", Link("/repos/Owner/repo/pulls", 2, "next")
                + (change == "terminal" ? ", " + Link("/repos/Owner/repo/pulls", 3, "last") : ""));
            return Task.FromResult(response);
        };
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code);
        Assert.Null(result.Value);
        Assert.Equal(change == "empty-next" ? 1 : 2, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("trailing")]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("zero-id")]
    [InlineData("exponent-id")]
    [InlineData("unknown-kind")]
    [InlineData("depth")]
    [InlineData("invalid-utf8")]
    [InlineData("mime")]
    [InlineData("encoding")]
    [InlineData("length")]
    [InlineData("overlength")]
    public async Task Malformed_or_unbounded_responses_never_escape_typed_boundary(string fault)
    {
        using var harness = await Harness.Create();
        var actor = Actor();
        var raw = actor.ToJsonString();
        if (fault == "duplicate") raw = raw[..^1] + ",\"id\":1}";
        if (fault == "nested-duplicate") raw = raw[..^1] + ",\"extra\":{\"x\":1,\"x\":2}}";
        if (fault == "trailing") raw += "{}";
        if (fault == "missing") { actor.Remove("id"); raw = actor.ToJsonString(); }
        if (fault == "wrong-type") raw = raw.Replace("\"id\":7", "\"id\":\"7\"", StringComparison.Ordinal);
        if (fault == "zero-id") raw = raw.Replace("\"id\":7", "\"id\":0", StringComparison.Ordinal);
        if (fault == "exponent-id") raw = raw.Replace("\"id\":7", "\"id\":7e0", StringComparison.Ordinal);
        if (fault == "unknown-kind") raw = raw.Replace("User", "Unknown", StringComparison.Ordinal);
        if (fault == "depth") raw = raw[..^1] + ",\"extra\":" + new string('[', 34) + "0" + new string(']', 34) + "}";
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json(raw);
            if (fault == "invalid-utf8") response.Content = new ByteArrayContent([0xff, 0xfe]);
            if (fault == "mime") response.Content.Headers.ContentType = new("text/html");
            if (fault == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (fault == "length") response.Content.Headers.ContentLength = 1;
            if (fault == "overlength") response.Content.Headers.ContentLength = GitHubResponseReader.MaximumBodyBytes + 1L;
            return Task.FromResult(response);
        };
        var result = await harness.Client.GetAuthenticatedUserAsync();
        Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code);
        Assert.Null(result.Value);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task Object_shape_identity_and_truncation_are_checked_including_redundant_urls()
    {
        using var harness = await Harness.Create();
        foreach (var fault in new[] { "sha", "mode", "type", "truncated", "duplicate-path", "root-url", "entry-url" })
        {
            var tree = Tree();
            if (fault == "sha") tree["sha"] = Oid('3');
            if (fault == "mode") tree["tree"]![0]!["mode"] = "100664";
            if (fault == "type") tree["tree"]![0]!["type"] = "commit";
            if (fault == "truncated") tree["truncated"] = true;
            if (fault == "duplicate-path") tree["tree"]!.AsArray().Add(tree["tree"]![0]!.DeepClone());
            if (fault == "root-url") tree["url"] = Origin + "repos/Owner/repo/git/trees/" + Oid('3');
            if (fault == "entry-url") tree["tree"]![0]!["url"] = "https://hostile.invalid/";
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(tree));
            Assert.Equal(GitHubFailureCode.InvalidResponse, (await harness.Client.GetTreeAsync(Oid('2'))).Failure!.Code);
        }
        foreach (var encoding in new[] { "utf-8", "base64" })
        {
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonObject
            { ["sha"] = Oid('2'), ["encoding"] = encoding, ["size"] = 3, ["content"] = "YQ==" }));
            Assert.Equal(GitHubFailureCode.InvalidResponse, (await harness.Client.GetBlobAsync(Oid('2'))).Failure!.Code);
        }
    }

    [Fact]
    public async Task Graphql_envelope_and_partial_errors_never_acknowledge()
    {
        using var harness = await Harness.Create();
        foreach (var raw in new[] { "{}", "{\"data\":null}", "{\"data\":{\"updateRefs\":null}}",
            "{\"data\":{\"updateRefs\":{\"clientMutationId\":\"wrong\"}}}", "{\"errors\":[]}",
            "{\"data\":null,\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"private\"}]}",
            "{\"data\":{\"updateRefs\":{\"clientMutationId\":\"wrong\"}},\"extra\":true}",
            "{\"errors\":[{\"type\":\"CONFLICT\",\"message\":\"private\"}],\"data\":{\"updateRefs\":{\"clientMutationId\":\"partial\"}}}" })
        {
            harness.Handler.Reply = async (request, token) => { await CaptureBody(request, token); return Json(raw); };
            var result = await harness.Client.UpdateRefAsync(Update(harness.Authority));
            Assert.NotNull(result.Failure);
            Assert.Equal(GitHubDelivery.Ambiguous, result.Delivery);
            Assert.IsType<GitHubRefContext>(result.Context);
            Assert.Null(result.Value);
        }
        foreach (var types in new[] { new[] { "CONFLICT", "FORBIDDEN" }, new[] { "FORBIDDEN", "CONFLICT" } })
        {
            using var document = GitHubResponseReader.Parse(Encoding.UTF8.GetBytes(new JsonObject
            { ["errors"] = new JsonArray(types.Select(type => (JsonNode)new JsonObject { ["type"] = type, ["message"] = "private" }).ToArray()) }.ToJsonString()));
            Assert.Equal(GitHubFailureCode.InvalidResponse, Assert.Throws<GitHubProtocolException>(() => GitHubResponseReader.GraphQl(document.RootElement, "expected")).Code);
        }
    }

    private static void Keys(JsonElement element, params string[] keys) =>
        Assert.Equal(keys.Order(StringComparer.Ordinal), element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("a.LOCK")]
    [InlineData("a./branch")]
    [InlineData("a\u00a0b")]
    [InlineData("a\u0085b")]
    [InlineData("a\u2003b")]
    public async Task Wire_valid_foreign_refs_are_observable_but_never_grant_write_authority(string branch)
    {
        using var harness = await Harness.Create();
        var fullRef = "refs/heads/" + branch;
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonObject
        { ["ref"] = fullRef, ["node_id"] = "REF_1", ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = Oid('2') } }));
        var reference = await harness.Client.GetRefAsync(fullRef);
        Assert.Null(reference.Failure);
        Assert.Equal(fullRef, reference.Value!.Name);
        Assert.Equal("/repos/Owner/repo/git/ref/heads/" + string.Join('/', branch.Split('/').Select(Uri.EscapeDataString)),
            Assert.Single(harness.Handler.Requests).Path);

        var pull = Pull();
        pull["base"]!["ref"] = branch;
        pull["head"]!["ref"] = branch;
        pull["head"]!["repo"] = Repository("Other", "fork", 99, "R_99");
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(pull));
        var detail = await harness.Client.GetPullRequestAsync(1);
        Assert.Null(detail.Failure);
        Assert.Equal(branch, detail.Value!.BaseRef);
        Assert.Equal(branch, detail.Value.Head.Ref);
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonArray(pull.DeepClone())));
        var list = await harness.Client.ListPullRequestsAsync();
        Assert.Null(list.Failure);
        Assert.Equal(branch, Assert.Single(list.Value!.Items).Head.Ref);

        harness.Handler.Requests.Clear();
        Assert.Equal(GitHubFailureCode.InvalidRequest,
            (await harness.Client.UpdateRefAsync(new(fullRef, Oid('1'), Oid('2'), false))).Failure!.Code);
        Assert.Equal(GitHubFailureCode.InvalidRequest,
            (await harness.Client.CreatePullRequestAsync(PullRequestInput(harness.Authority) with { HeadRef = fullRef })).Failure!.Code);
        Assert.Equal(GitHubFailureCode.InvalidRequest,
            (await harness.Client.CreatePullRequestAsync(PullRequestInput(harness.Authority) with { BaseRef = fullRef })).Failure!.Code);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("\t-control.md")]
    [InlineData("\"quote.md")]
    [InlineData("文-bmp.md")]
    [InlineData("😀-non-bmp.md")]
    [InlineData("existing\nname.md")]
    [InlineData("existing\\name.md")]
    public async Task Git_tree_names_remain_exact_json_data_not_ref_or_host_path_policy(string name)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = async (request, token) =>
        {
            if (request.Content is not null) await CaptureBody(request, token);
            var tree = Tree();
            tree["tree"]![0]!["path"] = name;
            return Json(tree, request.Method == HttpMethod.Get ? 200 : 201);
        };
        Assert.Equal(name, Assert.Single((await harness.Client.GetTreeAsync(Oid('2'))).Value!.Entries).Path);
        var created = await harness.Client.CreateTreeAsync(Oid('2'), [new(name, GitHubTreeMode.File, Oid('4'), null)]);
        Assert.Null(created.Failure);
        Assert.Equal(name, Assert.Single(created.Value!.Entries).Path);
        using var body = JsonDocument.Parse(harness.Handler.Requests[^1].Body!);
        Assert.Equal(name, body.RootElement.GetProperty("tree")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Failed_page_preserves_permission_observations_and_body_loss_respects_received_status_and_rate_hints()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) =>
        {
            var response = Json("{}", 403);
            response.Headers.Add("X-Accepted-GitHub-Permissions", "pull_requests=read");
            return Task.FromResult(response);
        };
        var page = await harness.Client.ListPullRequestsAsync();
        Assert.Equal(GitHubFailureCode.Permission, page.Failure!.Code);
        Assert.Equal(GitHubDelivery.Read, page.Delivery);
        Assert.Equal(GitHubPermission.PullRequests, Assert.Single(Assert.Single(page.RequiredPermissions!.Alternatives)).Permission);
        foreach (var variant in new[] { "auth", "permission", "rate", "retry-after", "remaining" })
        {
            harness.Handler.Requests.Clear();
            harness.Handler.Reply = (_, _) =>
            {
                var status = variant switch { "auth" => 401, "permission" => 403, "rate" => 429, _ => 200 };
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(new FaultStream()) };
                if (variant == "retry-after") response.Headers.Add("Retry-After", "1");
                if (variant == "remaining") response.Headers.Add("X-RateLimit-Remaining", "0");
                return Task.FromResult(response);
            };
            Assert.Equal(GitHubFailureCode.ResponseLost, (await harness.Client.GetAuthenticatedUserAsync()).Failure!.Code);
            Assert.Single(harness.Handler.Requests);
        }
    }

    [Fact]
    public async Task Blob_exact_limit_is_supported_and_overlimit_request_never_dispatches()
    {
        using var harness = await Harness.Create();
        var bytes = new byte[GitHubResponseReader.MaximumBlobBytes];
        bytes[^1] = 0xff;
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonObject
        { ["sha"] = Oid('2'), ["size"] = bytes.Length, ["encoding"] = "base64", ["content"] = Convert.ToBase64String(bytes) }));
        var result = await harness.Client.GetBlobAsync(Oid('2'));
        Assert.Null(result.Failure);
        Assert.Equal(bytes, result.Value!.Bytes.ToArray());
        harness.Handler.Requests.Clear();
        var over = await harness.Client.CreateBlobAsync(Oid('2'), new byte[bytes.Length + 1]);
        Assert.Equal(GitHubFailureCode.InvalidRequest, over.Failure!.Code);
        Assert.Empty(harness.Handler.Requests);
        harness.Handler.Reply = Success;
        Assert.Equal(GitHubDelivery.NeedsReadback, (await harness.Client.CreateBlobAsync(Oid('2'), bytes)).Delivery);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task Undeclared_stream_length_is_bounded_and_body_deadline_preserves_write_context()
    {
        using (var harness = await Harness.Create())
        {
            using var stream = new BoundedProbeStream(GitHubResponseReader.MaximumBodyBytes + 100L);
            harness.Handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            var result = await harness.Client.GetAuthenticatedUserAsync();
            Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code);
            Assert.Equal(GitHubResponseReader.MaximumBodyBytes + 1L, stream.BytesRead);
            Assert.Single(harness.Handler.Requests);
        }
        using (var harness = await Harness.Create(timeout: 50))
        {
            harness.Handler.Reply = async (request, token) =>
            {
                await CaptureBody(request, token);
                return new(HttpStatusCode.Created) { Content = new StreamContent(new BoundedProbeStream(0, hang: true)) };
            };
            var result = await Mutate(harness, 0);
            Assert.Equal(GitHubFailureCode.Timeout, result.Failure!.Code);
            Assert.Equal(GitHubDelivery.Ambiguous, result.Delivery);
            Assert.IsType<GitHubObjectContext>(result.Context);
            Assert.Single(harness.Handler.Requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Page_limit_requires_a_real_terminal_page(bool over)
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (request, _) =>
        {
            var page = int.Parse(request.RequestUri!.Query.Split("page=")[^1], System.Globalization.CultureInfo.InvariantCulture);
            var response = Json(new JsonArray(Pull()));
            if (page < GitHubResponseReader.MaximumPages || over)
                response.Headers.Add("Link", Link("/repos/Owner/repo/pulls", page + 1, "next"));
            return Task.FromResult(response);
        };
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Equal(100, harness.Handler.Requests.Count);
        if (over) { Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code); Assert.Null(result.Value); }
        else { Assert.Null(result.Failure); Assert.Equal(100, result.Value!.Pages); Assert.True(result.Value.Exhausted); }
    }

    [Fact]
    public async Task Aggregate_page_bytes_and_per_page_counts_fail_without_partial_results()
    {
        using var harness = await Harness.Create();
        harness.Handler.Reply = (_, _) => Task.FromResult(Json(new JsonArray(Enumerable.Range(1, 101).Select(number => (JsonNode)Pull(number)).ToArray())));
        Assert.Equal(GitHubFailureCode.InvalidResponse, (await harness.Client.ListPullRequestsAsync()).Failure!.Code);
        Assert.Single(harness.Handler.Requests);
        harness.Handler.Requests.Clear();
        var padding = new string('x', 17 * 1024 * 1024);
        harness.Handler.Reply = (request, _) =>
        {
            var page = int.Parse(request.RequestUri!.Query.Split("page=")[^1], System.Globalization.CultureInfo.InvariantCulture);
            var pull = Pull(page);
            pull["unused_additive_padding"] = padding;
            var response = Json(new JsonArray(pull));
            response.Headers.Add("Link", Link("/repos/Owner/repo/pulls", page + 1, "next"));
            return Task.FromResult(response);
        };
        var result = await harness.Client.ListPullRequestsAsync();
        Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure!.Code);
        Assert.Null(result.Value);
        Assert.Equal(4, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Tree_count_and_preencoding_bounds_are_checked_before_writer_allocation()
    {
        using var harness = await Harness.Create();
        var count = Enumerable.Repeat(new GitHubTreeEntry("a", GitHubTreeMode.File, Oid('2'), null), 100_001).ToImmutableArray();
        Assert.Equal(GitHubFailureCode.InvalidRequest, (await harness.Client.CreateTreeAsync(Oid('2'), count)).Failure!.Code);
        var escaping = Enumerable.Range(0, 5000).Select(index => new GitHubTreeEntry(new string('文', 1000) + index,
            GitHubTreeMode.File, Oid('2'), null)).ToImmutableArray();
        Assert.Equal(GitHubFailureCode.InvalidRequest, (await harness.Client.CreateTreeAsync(Oid('2'), escaping)).Failure!.Code);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Read_shapes_cover_root_merge_commits_tree_modes_and_impossible_pr_states()
    {
        using var harness = await Harness.Create();
        foreach (var parentCount in new[] { 0, 2 })
        {
            var commit = Commit();
            commit["parents"] = new JsonArray(Enumerable.Range(0, parentCount)
                .Select(index => (JsonNode)new JsonObject { ["sha"] = Oid((char)('3' + index)) }).ToArray());
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(commit));
            Assert.Equal(parentCount, (await harness.Client.GetCommitAsync(Oid('2'))).Value!.Parents.Length);
        }
        foreach (var mode in Enum.GetValues<GitHubTreeMode>())
        {
            var tree = Tree();
            tree["tree"]![0]!["mode"] = GitHubResponseReader.WireMode(mode);
            tree["tree"]![0]!["type"] = GitHubResponseReader.ObjectType(mode);
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(tree));
            Assert.Equal(mode, Assert.Single((await harness.Client.GetTreeAsync(Oid('2'))).Value!.Entries).Mode);
        }
        foreach (var fault in new[] { "merged", "closed_at", "state", "base", "head-sha", "head-ref", "number" })
        {
            var pull = Pull();
            if (fault == "merged") pull["merged"] = true;
            if (fault == "closed_at") pull["closed_at"] = "2026-09-03T08:00:00Z";
            if (fault == "state") pull["state"] = "unknown";
            if (fault == "base") pull["base"]!["repo"] = null;
            if (fault == "head-sha") pull["head"]!["sha"] = "not-an-oid";
            if (fault == "head-ref") pull["head"]!["ref"] = 1;
            if (fault == "number") pull["number"] = 2;
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(pull));
            Assert.Equal(GitHubFailureCode.InvalidResponse, (await harness.Client.GetPullRequestAsync(1)).Failure!.Code);
        }
    }

    [Fact]
    public async Task Disposed_client_releases_credential_and_cannot_dispatch()
    {
        using var harness = await Harness.Create();
        harness.Client.Dispose();
        Assert.Null(typeof(GitHubApiClient).GetField("credential", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(harness.Client));
        Assert.Equal(GitHubFailureCode.InvalidRequest, (await harness.Client.GetAuthenticatedUserAsync()).Failure!.Code);
        Assert.Empty(harness.Handler.Requests);
    }

    private sealed class BoundedProbeStream(long length, bool hang = false) : Stream
    {
        internal long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(length - BytesRead, count);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(length - BytesRead, buffer.Length);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static IDisposable Register(Uri uri, HttpMessageHandler handler, int timeout = 30_000) =>
        (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [uri, handler, timeout])!;

    private static ValidatedGitHubPublicationAuthority Authority(string owner = "Owner", string name = "repo", string operation = "operation-1") =>
        GitHubPublicationFactory.CreateAuthority(new(
            owner, name, "refs/heads/main", Oid('1'), "campaign-1", Hash('1'), Hash('2'), Hash('3'), 7,
            Hash('4'), Hash('5'), Hash('6'), Hash('7'), Hash('8'), operation, "generation-1",
            null, null, null, null, null, null, null, GitHubPublicationTransitionKind.Initial,
            new(10, 10, 1000), new(10, 10, 1000), [new("docs/readme.md", Hash('1'), Hash('2'), 1, 10, 12, 1, 1)], []));

    private static GitHubCreateCommit CommitRequest() => new(Oid('2'), Oid('3'), Oid('1'), "private commit message",
        new("Synthetic", "synthetic@example.invalid", DateTimeOffset.Parse("2026-09-03T08:00:00Z")),
        new("Synthetic", "synthetic@example.invalid", DateTimeOffset.Parse("2026-09-03T08:00:00Z")));
    private static GitHubCreatePullRequest PullRequestInput(ValidatedGitHubPublicationAuthority authority) =>
        new(Hash('a'), GitHubPublicationFactory.CreateProposalRef(authority), Oid('2'), "refs/heads/main", Oid('1'), "private title", "private body");
    private static GitHubUpdateRef Update(ValidatedGitHubPublicationAuthority authority) =>
        new(GitHubPublicationFactory.CreateCoordinationRef(authority), Oid('0'), Oid('2'), true);

    private static async Task<MutationObservation> Mutate(Harness harness, int family, CancellationToken token = default) => family switch
    {
        0 => Observe(await harness.Client.CreateBlobAsync(Oid('2'), "private candidate bytes"u8.ToArray(), token)),
        1 => Observe(await harness.Client.CreateTreeAsync(Oid('2'), [new("readme.md", GitHubTreeMode.File, Oid('4'), null)], token)),
        2 => Observe(await harness.Client.CreateCommitAsync(CommitRequest(), token)),
        3 => Observe(await harness.Client.CreatePullRequestAsync(PullRequestInput(harness.Authority), token)),
        4 => Observe(await harness.Client.UpdateRefAsync(Update(harness.Authority), token)),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
    private static MutationObservation Observe<T>(GitHubApiResult<T> result) where T : class => new(result.Failure, result.Delivery, result.Context, result.Value);
    private sealed record MutationObservation(GitHubFailure? Failure, GitHubDelivery Delivery, GitHubMutationContext? Context, object? Value);

    private static JsonObject Actor(string login = "owner", long id = 7) => new()
    { ["id"] = id, ["node_id"] = "U_" + id, ["login"] = login, ["type"] = "User" };
    private static JsonObject Repository(string owner = "Owner", string name = "repo", long id = 42, string node = "R_42") => new()
    {
        ["id"] = id,
        ["node_id"] = node,
        ["owner"] = Actor(owner),
        ["name"] = name,
        ["full_name"] = owner + "/" + name,
        ["private"] = true,
        ["archived"] = false,
        ["disabled"] = false
    };
    private static JsonObject Tree() => new()
    {
        ["sha"] = Oid('2'),
        ["truncated"] = false,
        ["tree"] = new JsonArray(new JsonObject
        { ["path"] = "readme.md", ["mode"] = "100644", ["type"] = "blob", ["sha"] = Oid('4'), ["size"] = 3 })
    };
    private static JsonObject Commit() => new()
    {
        ["sha"] = Oid('2'),
        ["tree"] = new JsonObject { ["sha"] = Oid('3') },
        ["parents"] = new JsonArray(new JsonObject { ["sha"] = Oid('1') }),
        ["message"] = "private commit message",
        ["author"] = CommitActor(),
        ["committer"] = CommitActor()
    };
    private static JsonObject CommitActor() => new()
    { ["name"] = "Synthetic", ["email"] = "synthetic@example.invalid", ["date"] = "2026-09-03T08:00:00Z" };
    private static JsonObject Pull(int number = 1) => new()
    {
        ["id"] = 1000 + number,
        ["node_id"] = "PR_" + number,
        ["number"] = number,
        ["state"] = "open",
        ["draft"] = true,
        ["merged"] = false,
        ["merged_at"] = null,
        ["closed_at"] = null,
        ["created_at"] = "2026-09-03T08:00:00Z",
        ["title"] = "private title",
        ["body"] = "private body",
        ["user"] = Actor(),
        ["maintainer_can_modify"] = false,
        ["base"] = new JsonObject { ["repo"] = Repository(), ["ref"] = "main", ["sha"] = Oid('1') },
        ["head"] = new JsonObject { ["repo"] = Repository(), ["ref"] = "proposal", ["sha"] = Oid('2') }
    };
    private static string Link(string path, int page, string relation) => "<" + Origin.TrimEnd('/') + path
        + "?state=all&sort=created&direction=asc&per_page=100&page=" + page + ">; rel=\"" + relation + "\"";
    private static HttpResponseMessage Json(JsonNode value, int status = 200) => Json(value.ToJsonString(), status);
    private static HttpResponseMessage Json(string value, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static readonly HttpRequestOptionsKey<string> BodyKey = new("synthetic-body");
    private static async Task CaptureBody(HttpRequestMessage request, CancellationToken token)
    {
        using var stream = new MemoryStream();
        await request.Content!.CopyToAsync(stream, token);
        request.Options.Set(BodyKey, Encoding.UTF8.GetString(stream.ToArray()));
    }
    private static async Task<HttpResponseMessage> Success(HttpRequestMessage request, CancellationToken token)
    { await CaptureBody(request, token); return await SuccessFromCaptured(request); }
    private static Task<HttpResponseMessage> SuccessFromCaptured(HttpRequestMessage request)
    {
        request.Options.TryGetValue(BodyKey, out var text);
        using var document = JsonDocument.Parse(text!);
        var path = request.RequestUri!.AbsolutePath;
        var response = path switch
        {
            "/graphql" => Json(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["updateRefs"] = new JsonObject
                    { ["clientMutationId"] = document.RootElement.GetProperty("variables").GetProperty("input").GetProperty("clientMutationId").GetString() }
                }
            }),
            var p when p.EndsWith("/blobs", StringComparison.Ordinal) => Json(new JsonObject { ["sha"] = Oid('2') }, 201),
            var p when p.EndsWith("/trees", StringComparison.Ordinal) => Json(Tree(), 201),
            var p when p.EndsWith("/commits", StringComparison.Ordinal) => Json(Commit(), 201),
            _ => Json(Pull(), 201),
        };
        return Task.FromResult(response);
    }

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable registration;
        internal ScriptedHandler Handler { get; } = new();
        internal ValidatedGitHubPublicationAuthority Authority { get; }
        internal GitHubApiClient Client { get; }
        private Harness(string owner, string name, string operation, int timeout)
        {
            Authority = GitHubApiClientTests.Authority(owner, name, operation);
            registration = Register(new Uri(Origin), Handler, timeout);
            Client = GitHubApiClient.Create(Authority, GitHubTransportTestHook.Placeholder);
        }
        internal static async Task<Harness> Create(string owner = "Owner", string name = "repo", string operation = "operation-1",
            int timeout = 30_000, long repositoryId = 42, string repositoryNode = "R_42")
        {
            var harness = new Harness(owner, name, operation, timeout);
            harness.Handler.Reply = (_, _) => Task.FromResult(Json(Repository(id: repositoryId, node: repositoryNode)));
            var bound = await harness.Client.GetRepositoryAsync();
            Assert.Null(bound.Failure);
            harness.Handler.Requests.Clear();
            harness.Handler.Reply = (_, _) => Task.FromResult(Json("{}"));
            return harness;
        }
        public void Dispose() { Client.Dispose(); registration.Dispose(); }
    }
    private sealed record RequestObservation(string Method, string Path, Version Version, HttpVersionPolicy VersionPolicy,
        bool? ExpectContinue, bool AuthorizationPresent, string ApiVersion, string? ContentType)
    { internal string? Body { get; set; } }
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        internal List<RequestObservation> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) => Task.FromResult(Json("{}"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Retain presence only, never credentials or credential digests.
            var observation = new RequestObservation(request.Method.Method, request.RequestUri!.PathAndQuery, request.Version,
                request.VersionPolicy, request.Headers.ExpectContinue, request.Headers.Authorization?.Scheme == "Bearer",
                request.Headers.GetValues("X-GitHub-Api-Version").Single(), request.Content?.Headers.ContentType?.ToString());
            Requests.Add(observation);
            try { return await Reply(request, cancellationToken); }
            finally { if (request.Options.TryGetValue(BodyKey, out var body)) observation.Body = body; }
        }
    }
    private sealed class FaultStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("private lost response");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("private lost response");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
