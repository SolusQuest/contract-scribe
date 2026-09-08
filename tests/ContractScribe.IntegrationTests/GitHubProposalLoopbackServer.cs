using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

// A bounded wire-level GitHub model. It has no reference to adapter internals, campaign
// capabilities, H1, or R6 results. Object identities use Git's public object format.
internal sealed class GitHubProposalLoopbackServer : IAsyncDisposable
{
    internal sealed record Entry(string Path, string Mode, string Oid);
    internal sealed record Request(string Method, string Path, string Kind, int Mutation);
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> handlers = [];
    private readonly ConcurrentQueue<Exception> failures = [];
    private readonly Task loop;
    internal readonly object Gate = new();
    internal readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Entry[]> Trees = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonObject> Commits = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> Refs = new(StringComparer.Ordinal);
    internal readonly List<JsonObject> PullRequests = [];
    internal readonly List<Request> Requests = [];
    internal Uri Endpoint { get; }
    internal string BaseOid { get; }
    private readonly HashSet<string> baseBlobs, baseTrees, baseCommits;
    internal int Mutations;
    internal int SuccessfulCas;
    internal int PrAttempts;
    internal int? LoseAt { get; set; }
    internal bool LoseAfter { get; set; }
    internal string? RejectPath { get; set; }
    internal int RejectionStatus = 403;
    internal bool DelayResponses { get; set; }
    internal bool IncompletePagination { get; set; }
    internal Action<Request>? Before { get; set; }
    internal Action<Request>? After { get; set; }
    private string? barrierPath;
    private int barrierCount;
    private TaskCompletionSource? barrier;

    internal GitHubProposalLoopbackServer(IReadOnlyDictionary<string, byte[]> files)
    {
        BaseOid = AddCommit(new JsonObject
        {
            ["tree"] = BuildTree(files),
            ["parents"] = new JsonArray(new string('1', 40)),
            ["message"] = "Synthetic immutable base\n",
            ["author"] = Actor(),
            ["committer"] = Actor(),
        });
        Refs["refs/heads/main"] = BaseOid;
        baseBlobs = Blobs.Keys.ToHashSet(StringComparer.Ordinal);
        baseTrees = Trees.Keys.ToHashSet(StringComparer.Ordinal);
        baseCommits = Commits.Keys.ToHashSet(StringComparer.Ordinal);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        Endpoint = new Uri($"http://127.0.0.1:{number}/");
        listener.Prefixes.Add(Endpoint.AbsoluteUri);
        listener.Start();
        loop = ListenAsync();
    }

    internal void Barrier(string path, int count = 2)
    {
        lock (Gate)
        {
            barrierPath = path; barrierCount = count;
            barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void Reset()
    {
        lock (Gate)
        {
            foreach (var key in Blobs.Keys.Except(baseBlobs).ToArray()) Blobs.Remove(key);
            foreach (var key in Trees.Keys.Except(baseTrees).ToArray()) Trees.Remove(key);
            foreach (var key in Commits.Keys.Except(baseCommits).ToArray()) Commits.Remove(key);
            Refs.Clear(); Refs["refs/heads/main"] = BaseOid;
            PullRequests.Clear(); Requests.Clear();
            Mutations = SuccessfulCas = PrAttempts = 0;
            LoseAt = null; RejectPath = null; Before = After = null;
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
                handlers.Add(ServeAsync(await listener.GetContextAsync().WaitAsync(stop.Token)));
        }
        catch (Exception exception) when (stop.IsCancellationRequested
            && exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        { }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            Assert.Equal("2026-03-10", context.Request.Headers["X-GitHub-Api-Version"]);
            Assert.Equal("Bearer contract-scribe-synthetic-transport-only", context.Request.Headers["Authorization"]);
            Assert.InRange(context.Request.ContentLength64, 0, 32 * 1024 * 1024);
            var body = new byte[(int)context.Request.ContentLength64];
            await context.Request.InputStream.ReadExactlyAsync(body, stop.Token);
            var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
            var method = context.Request.HttpMethod;
            Assert.True(method is "GET" or "POST", "Unexpected mutation verb");
            var kind = path == "/graphql" ? "cas" : path.Split('/')[^1];
            Task? wait = null;
            (int Status, object Body) response;
            Request request;
            lock (Gate)
            {
                Assert.True(Requests.Count < 20_000);
                request = new(method, path, kind, method == "POST" ? ++Mutations : 0);
                Requests.Add(request);
                if (method == "POST" && path == "/repos/Owner/repo/pulls") PrAttempts++;
                Before?.Invoke(request);
                if (method == "POST" && request.Mutation == LoseAt && !LoseAfter)
                {
                    context.Response.Abort(); return;
                }
                response = IncompletePagination && path == "/repos/Owner/repo/pulls" && context.Request.QueryString["page"] == "2"
                    ? (503, new { message = "synthetic incomplete page" })
                    : RejectPath is not null && path.Contains(RejectPath, StringComparison.Ordinal)
                    ? (RejectionStatus, new { message = "synthetic rejection" })
                    : Dispatch(method, path, body);
                After?.Invoke(request);
                if (method == "POST" && request.Mutation == LoseAt && LoseAfter)
                {
                    context.Response.Abort(); return;
                }
                if (method == "GET" && barrierCount > 0 && path.Contains(barrierPath!, StringComparison.Ordinal))
                {
                    // Serialize while locked, before releasing either identical read observation.
                    response = (response.Status, JsonSerializer.SerializeToElement(response.Body));
                    wait = barrier!.Task;
                    if (--barrierCount == 0) barrier.TrySetResult();
                }
            }
            if (wait is not null) await wait.WaitAsync(TimeSpan.FromSeconds(45), stop.Token);
            if (DelayResponses) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response.Body);
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = "application/json";
            if (IncompletePagination && path == "/repos/Owner/repo/pulls" && context.Request.QueryString["page"] != "2")
            {
                var next = new Uri(Endpoint, "repos/Owner/repo/pulls?state=all&per_page=100&page=2").AbsoluteUri;
                context.Response.Headers["Link"] = "<" + next + ">; rel=\"next\", <" + next + ">; rel=\"last\"";
            }
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
            context.Response.Close();
        }
        catch (Exception exception) when (stop.IsCancellationRequested
            && exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        { }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
            context.Response.Abort();
        }
    }

    private (int, object) Dispatch(string method, string path, byte[] bytes)
    {
        if (method == "GET") return Read(path);
        var body = JsonNode.Parse(bytes)!.AsObject();
        if (path == "/repos/Owner/repo/git/blobs")
        {
            Assert.Equal("base64", body["encoding"]!.GetValue<string>());
            return (201, new { sha = AddBlob(Convert.FromBase64String(body["content"]!.GetValue<string>())) });
        }
        if (path == "/repos/Owner/repo/git/trees")
        {
            var oid = AddTree(body["tree"]!.AsArray().Select(e => new Entry(
                e!["path"]!.GetValue<string>(), e["mode"]!.GetValue<string>(), e["sha"]!.GetValue<string>())).ToArray());
            return (201, TreeResponse(oid));
        }
        if (path == "/repos/Owner/repo/git/commits") return (201, Commits[AddCommit(body)]);
        if (path == "/graphql")
        {
            Assert.Equal("mutation($input:UpdateRefsInput!){updateRefs(input:$input){clientMutationId}}", body["query"]!.GetValue<string>());
            var input = body["variables"]!["input"]!;
            Assert.Equal("REPO_node", input["repositoryId"]!.GetValue<string>());
            var update = Assert.Single(input["refUpdates"]!.AsArray())!;
            var name = update["name"]!.GetValue<string>();
            Assert.StartsWith("refs/heads/contract-scribe/", name);
            Assert.False(update["force"]!.GetValue<bool>());
            var after = update["afterOid"]!.GetValue<string>();
            Assert.NotEqual(new string('0', 40), after);
            Assert.True(Commits.ContainsKey(after));
            if (Refs.GetValueOrDefault(name, new string('0', 40)) != update["beforeOid"]!.GetValue<string>())
                return (200, new { data = (object?)null, errors = new[] { new { message = "conflict", type = "CONFLICT" } } });
            Refs[name] = after;
            SuccessfulCas++;
            return (200, new { data = new { updateRefs = new { clientMutationId = input["clientMutationId"]!.GetValue<string>() } } });
        }
        Assert.Equal("/repos/Owner/repo/pulls", path);
        Assert.True(body["draft"]!.GetValue<bool>());
        Assert.False(body["maintainer_can_modify"]!.GetValue<bool>());
        if (PullRequests.Any(pr => pr["state"]!.GetValue<string>() == "open"))
            return (422, new { message = "duplicate" });
        var number = PullRequests.Count + 1;
        var head = body["head"]!.GetValue<string>();
        var pr = JsonSerializer.SerializeToNode(new
        {
            id = number,
            node_id = "PR_" + number,
            number,
            state = "open",
            draft = true,
            merged = false,
            merged_at = (string?)null,
            closed_at = (string?)null,
            created_at = "2026-01-01T00:00:00Z",
            title = body["title"]!.GetValue<string>(),
            body = body["body"]!.GetValue<string>(),
            user = new { id = 41898282, node_id = "MDM6Qm90NDE4OTgyODI=", login = "github-actions[bot]", type = "Bot" },
            head = new { @ref = head, sha = Refs["refs/heads/" + head], repo = Repository() },
            @base = new { @ref = body["base"]!.GetValue<string>(), sha = Refs["refs/heads/main"], repo = Repository() },
            maintainer_can_modify = false,
        })!.AsObject();
        PullRequests.Add(pr);
        return (201, pr);
    }

    private (int, object) Read(string path)
    {
        if (path == "/repos/Owner/repo") return (200, Repository());
        if (path.StartsWith("/repos/Owner/repo/pulls", StringComparison.Ordinal))
        {
            foreach (var pr in PullRequests)
            {
                pr["head"]!["sha"] = Refs["refs/heads/" + pr["head"]!["ref"]!.GetValue<string>()];
                if (pr["state"]!.GetValue<string>() == "open") pr["base"]!["sha"] = Refs["refs/heads/main"];
            }
            if (path == "/repos/Owner/repo/pulls") return (200, PullRequests.ToArray());
            var match = PullRequests.SingleOrDefault(pr => pr["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) == path.Split('/')[^1]);
            return match is null ? Missing() : (200, match);
        }
        const string refsPrefix = "/repos/Owner/repo/git/ref/";
        if (path.StartsWith(refsPrefix, StringComparison.Ordinal))
        {
            var name = "refs/" + path[refsPrefix.Length..];
            return Refs.TryGetValue(name, out var oid)
                ? (200, new { @ref = name, node_id = "REF_node", @object = new { type = "commit", sha = oid } }) : Missing();
        }
        var key = path.Split('/')[^1];
        if (path.Contains("/git/blobs/", StringComparison.Ordinal) && Blobs.TryGetValue(key, out var bytes))
            return (200, new { sha = key, encoding = "base64", size = bytes.Length, content = Convert.ToBase64String(bytes) });
        if (path.Contains("/git/trees/", StringComparison.Ordinal) && Trees.ContainsKey(key)) return (200, TreeResponse(key));
        if (path.Contains("/git/commits/", StringComparison.Ordinal) && Commits.TryGetValue(key, out var commit)) return (200, commit);
        return Missing();
    }

    internal void Lifecycle(string kind)
    {
        lock (Gate)
        {
            var pr = PullRequests[0];
            var closed = kind is "closed" or "merged";
            pr["state"] = closed ? "closed" : "open"; pr["draft"] = kind == "draft";
            pr["merged"] = kind == "merged";
            pr["closed_at"] = closed ? "2026-01-02T00:00:00Z" : null;
            pr["merged_at"] = kind == "merged" ? "2026-01-02T00:00:00Z" : null;
        }
    }

    internal JsonObject Coordination()
    {
        var head = Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal)).Value;
        var root = Trees[Commits[head]["tree"]!["sha"]!.GetValue<string>()];
        return JsonNode.Parse(Blobs[Trees[root[0].Oid][0].Oid])!.AsObject();
    }

    private static object Repository() => new
    {
        id = 1,
        node_id = "REPO_node",
        name = "repo",
        full_name = "Owner/repo",
        @private = false,
        archived = false,
        disabled = false,
        owner = new { id = 1, node_id = "OWNER_node", login = "Owner", type = "Organization" },
    };
    private object TreeResponse(string oid) => new
    {
        sha = oid,
        truncated = false,
        tree = Trees[oid].Select(e => new
        {
            path = e.Path,
            mode = e.Mode,
            sha = e.Oid,
            type = e.Mode is "040000" or "40000" ? "tree" : e.Mode == "160000" ? "commit" : "blob",
        }).ToArray(),
    };
    private static (int, object) Missing() => (404, new { message = "Not Found" });
    private static JsonObject Actor() => new()
    {
        ["name"] = "Synthetic",
        ["email"] = "synthetic@example.invalid",
        ["date"] = "2000-01-01T00:00:00Z",
    };
    private string BuildTree(IReadOnlyDictionary<string, byte[]> files) => AddTree(files.GroupBy(p => p.Key.Split('/')[0])
        .Select(group => group.Key == group.First().Key
            ? new Entry(group.Key, "100644", AddBlob(group.Single().Value))
            : new Entry(group.Key, "040000", BuildTree(group.ToDictionary(p => p.Key[(group.Key.Length + 1)..], p => p.Value)))).ToArray());
    internal string AddBlob(byte[] bytes)
    {
        var oid = ObjectOid("blob", bytes); Blobs[oid] = bytes; return oid;
    }
    internal string AddTree(Entry[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var e in entries.OrderBy(e => e.Path + (e.Mode is "040000" or "40000" ? "/" : "\0"), StringComparer.Ordinal))
        {
            stream.Write(Encoding.UTF8.GetBytes((e.Mode == "040000" ? "40000" : e.Mode) + " " + e.Path + "\0"));
            stream.Write(Convert.FromHexString(e.Oid));
        }
        var oid = ObjectOid("tree", stream.ToArray()); Trees[oid] = entries; return oid;
    }
    internal string AddCommit(JsonObject request)
    {
        var tree = request["tree"]!.GetValue<string>();
        var parents = request["parents"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
        var message = request["message"]!.GetValue<string>();
        static string ActorLine(string role, JsonNode actor) => role + " " + actor["name"]!.GetValue<string>()
            + " <" + actor["email"]!.GetValue<string>() + "> "
            + DateTimeOffset.Parse(actor["date"]!.GetValue<string>(), CultureInfo.InvariantCulture).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000\n";
        var text = "tree " + tree + "\n" + string.Concat(parents.Select(p => "parent " + p + "\n"))
            + ActorLine("author", request["author"]!) + ActorLine("committer", request["committer"]!) + "\n" + message;
        var oid = ObjectOid("commit", Encoding.UTF8.GetBytes(text));
        Commits[oid] = new JsonObject
        {
            ["sha"] = oid,
            ["tree"] = new JsonObject { ["sha"] = tree },
            ["parents"] = new JsonArray(parents.Select(p => (JsonNode)new JsonObject { ["sha"] = p }).ToArray()),
            ["message"] = message,
            ["author"] = request["author"]!.DeepClone(),
            ["committer"] = request["committer"]!.DeepClone(),
        };
        return oid;
    }
    private static string ObjectOid(string kind, byte[] bytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes(kind + " " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        sha.AppendData(bytes); return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Close();
        await loop;
        await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(10));
        stop.Dispose();
        Assert.Empty(failures);
    }
}
