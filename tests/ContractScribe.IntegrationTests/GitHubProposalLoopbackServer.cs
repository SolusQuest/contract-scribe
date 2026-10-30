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
internal sealed partial class GitHubProposalLoopbackServer : IAsyncDisposable
{
    internal sealed record Entry(string Path, string Mode, string Oid);
    internal sealed record Request(string Method, string Path, string Kind, int Mutation)
    {
        internal JsonObject? Body { get; init; }
        internal bool CurrentCheckpointQuery => Kind == "checkpoint-read" && Body?["variables"]?["anchor"] is null;
    }

    internal static byte[]? WrittenCanonicalBytes(Request request, string name)
    {
        if (request.Kind != "checkpoint-commit") return null;
        var additions = request.Body!["variables"]!["input"]!["fileChanges"]!["additions"]!.AsArray();
        var file = additions.SingleOrDefault(item => item!["path"]!.GetValue<string>() == ".contract-scribe/" + name);
        return file is null ? null : Convert.FromBase64String(file["contents"]!.GetValue<string>());
    }

    internal static JsonObject? WrittenCanonicalFile(Request request, string name) =>
        WrittenCanonicalBytes(request, name) is { } bytes ? JsonNode.Parse(bytes)!.AsObject() : null;
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
    internal int PublicationWrites;
    internal int? LoseAt { get; set; }
    internal bool LoseAfter { get; set; }
    private TimeSpan serverClockOffset;
    internal DateTimeOffset ServerNow
    {
        get => DateTimeOffset.UtcNow + serverClockOffset;
        set => serverClockOffset = value - DateTimeOffset.UtcNow;
    }
    internal string? RejectPath { get; set; }
    internal int RejectionStatus = 403;
    internal bool DelayResponses { get; set; }
    internal CancellationToken Stopping => stop.Token;
    internal bool IncompletePagination { get; set; }
    internal Action<Request, byte[]>? BeforeBody { get; set; }
    internal Action<Request>? Before { get; set; }
    internal Action<Request>? After { get; set; }
    internal Func<Request, Task?>? BeforeResponse { get; set; }
    private string? barrierPath;
    private int barrierCount;
    private TaskCompletionSource? barrier;

    internal GitHubProposalLoopbackServer(IReadOnlyDictionary<string, byte[]> files)
    {
        BaseOid = AddCommit(new JsonObject
        {
            ["tree"] = BuildTree(files),
            ["parents"] = new JsonArray(),
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
            Mutations = SuccessfulCas = PrAttempts = PublicationWrites = 0;
            LoseAt = null; RejectPath = null; Before = After = null; BeforeResponse = null;
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
            var query = method == "POST" && path == "/graphql"
                && JsonNode.Parse(body)!["query"]!.GetValue<string>().TrimStart().StartsWith("query", StringComparison.Ordinal);
            var campaignQuery = query && JsonNode.Parse(body)!["query"]!.GetValue<string>() == CampaignSnapshotQuery;
            var kind = campaignQuery ? "campaign-snapshot" : query ? "checkpoint-read" : path == "/graphql"
                ? JsonNode.Parse(body)!["query"]!.GetValue<string>().Contains("createCommitOnBranch", StringComparison.Ordinal) ? "checkpoint-commit" : "cas"
                : path.Split('/')[^1];
            Task? wait = null;
            (int Status, object Body) response;
            Request request;
            lock (Gate)
            {
                if (Requests.Count >= MaximumRequests)
                    Assert.Fail($"Request guard: {method} {path}; mutations={Mutations}; PR attempts={PrAttempts}; ledger={CoordinationDocument().ToJsonString()}");
                request = new(method, path, kind, method == "POST" && !query ? ++Mutations : 0)
                { Body = method == "POST" && path == "/graphql" ? JsonNode.Parse(body)!.AsObject() : null };
                Requests.Add(request);
                if (method == "POST" && path == "/repos/Owner/repo/pulls") PrAttempts++;
                BeforeBody?.Invoke(request, body);
                Before?.Invoke(request);
                if (request.Mutation > 0 && request.Mutation == LoseAt && !LoseAfter)
                {
                    context.Response.Abort(); return;
                }
                response = IncompletePagination && path == "/repos/Owner/repo/pulls" && context.Request.QueryString["page"] == "2"
                    ? (503, new { message = "synthetic incomplete page" })
                    : RejectPath is not null && path.Contains(RejectPath, StringComparison.Ordinal)
                    ? (RejectionStatus, new { message = "synthetic rejection" })
                    : Dispatch(method, path, body);
                After?.Invoke(request);
                if (request.Mutation > 0 && request.Mutation == LoseAt && LoseAfter)
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
                wait ??= BeforeResponse?.Invoke(request);
            }
            if (wait is not null) await wait.WaitAsync(TimeSpan.FromSeconds(45), stop.Token);
            if (DelayResponses) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response.Body);
            context.Response.StatusCode = response.Status;
            context.Response.Headers["Date"] = ServerNow.ToString("R", CultureInfo.InvariantCulture);
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
            var rows = body["tree"]!.AsArray();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in rows)
            {
                var name = entry!["path"]!.GetValue<string>();
                // This fixture models the producer's direct entries, not GitHub's
                // additional nested-path/base_tree construction API.
                Assert.DoesNotContain('/', name);
                var mode = entry["mode"]!.GetValue<string>();
                Assert.True(mode is "100644" or "100755" or "040000", "Unsupported fixture tree mode");
                var type = entry["type"]!.GetValue<string>();
                var reference = entry["sha"]!.GetValue<string>();
                var directory = mode == "040000";
                if (name is "" or "." or ".." || name.Contains('\0') || !paths.Add(name)
                    || type != (directory ? "tree" : "blob")
                    || !(directory ? Trees.ContainsKey(reference) : Blobs.ContainsKey(reference)))
                    return InvalidGraph();
            }
            var oid = AddTree(rows.Select(e => new Entry(
                e!["path"]!.GetValue<string>(), e["mode"]!.GetValue<string>(), e["sha"]!.GetValue<string>())).ToArray());
            return (201, TreeResponse(oid));
        }
        if (path == "/repos/Owner/repo/git/commits")
        {
            if (!Trees.ContainsKey(body["tree"]!.GetValue<string>())
                || body["parents"]!.AsArray().Any(parent => !Commits.ContainsKey(parent!.GetValue<string>())))
                return InvalidGraph();
            return (201, CommitResponse(Commits[AddCommit(body)]));
        }
        if (path == "/graphql")
        {
            if (body["query"]!.GetValue<string>() == CampaignSnapshotQuery)
                return (200, CampaignSnapshot(body["variables"]!.AsObject()));
            if (body["query"]!.GetValue<string>().TrimStart().StartsWith("query", StringComparison.Ordinal))
                return (200, CheckpointSnapshot(body["variables"]!.AsObject()));
            if (body["query"]!.GetValue<string>().Contains("createCommitOnBranch", StringComparison.Ordinal))
                return AtomicCheckpoint(body["variables"]!["input"]!.AsObject());
            Assert.Equal("mutation($input:UpdateRefsInput!){updateRefs(input:$input){clientMutationId}}", body["query"]!.GetValue<string>());
            var input = body["variables"]!["input"]!;
            Assert.Equal("REPO_node", input["repositoryId"]!.GetValue<string>());
            var updates = input["refUpdates"]!.AsArray();
            Assert.InRange(updates.Count, 1, 2);
            Assert.Equal(updates.Count, updates.Select(update => update!["name"]!.GetValue<string>()).Distinct().Count());
            foreach (var update in updates)
            {
                var name = update!["name"]!.GetValue<string>();
                Assert.StartsWith("refs/heads/contract-scribe/", name);
                Assert.False(update["force"]!.GetValue<bool>());
                var after = update["afterOid"]!.GetValue<string>();
                Assert.NotEqual(new string('0', 40), after);
                Assert.True(Commits.ContainsKey(after));
                if (Refs.GetValueOrDefault(name, new string('0', 40)) != update["beforeOid"]!.GetValue<string>())
                    return (200, new { data = (object?)null, errors = new[] { new { message = "conflict", type = "CONFLICT" } } });
            }
            var publicationChanged = updates.Any(update =>
            {
                var name = update!["name"]!.GetValue<string>();
                if (name.Contains("/proposals/", StringComparison.Ordinal)) return true;
                var after = DocumentAt(update["afterOid"]!.GetValue<string>())["publication"]?["oid"]?.GetValue<string>();
                var before = Refs.TryGetValue(name, out var head) ? DocumentAt(head)["publication"]?["oid"]?.GetValue<string>() : null;
                return before != after;
            });
            foreach (var update in updates)
                Refs[update!["name"]!.GetValue<string>()] = update["afterOid"]!.GetValue<string>();
            if (publicationChanged) PublicationWrites++;
            SuccessfulCas++;
            return (200, new { data = new { updateRefs = new { clientMutationId = input["clientMutationId"]!.GetValue<string>() } } });
        }
        Assert.Equal("/repos/Owner/repo/pulls", path);
        PublicationWrites++;
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

    // Public GraphQL field shapes over the same wire database as REST. Query
    // HTTP POSTs do not increment the mutation ordinal or trigger response-loss
    // injection for non-idempotent effects.
    private (int, object) AtomicCheckpoint(JsonObject input)
    {
        Assert.Equal("REF_node", input["branch"]!["id"]!.GetValue<string>());
        var before = input["expectedHeadOid"]!.GetValue<string>();
        var reference = Refs.Keys.Single(name => name.StartsWith("refs/heads/contract-scribe/coordination/", StringComparison.Ordinal));
        if (Refs[reference] != before)
            return (200, new { data = (object?)null, errors = new[] { new { message = "conflict", type = "CONFLICT" } } });
        var root = Trees[Commits[before]["tree"]!["sha"]!.GetValue<string>()];
        var leaf = Trees[root.Single().Oid].ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        foreach (var file in input["fileChanges"]!["additions"]!.AsArray())
        {
            var path = file!["path"]!.GetValue<string>();
            Assert.StartsWith(".contract-scribe/", path);
            Assert.DoesNotContain('/', path[17..]);
            leaf[path[17..]] = new(path[17..], "100644", AddBlob(Convert.FromBase64String(file["contents"]!.GetValue<string>())));
        }
        var tree = AddTree([new(".contract-scribe", "040000", AddTree(leaf.Values.ToArray()))]);
        var message = input["message"]!["headline"]!.GetValue<string>() + "\n\n" + input["message"]!["body"]!.GetValue<string>();
        var seconds = ServerNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = "tree " + tree + "\nparent " + before
            + "\nauthor github-actions[bot] <41898282+github-actions[bot]@users.noreply.github.com> " + seconds
            + " +0000\ncommitter GitHub <noreply@github.com> " + seconds + " +0000\n\n" + message;
        const string signature = "-----BEGIN PGP SIGNATURE-----\nsynthetic-wire-fixture\n-----END PGP SIGNATURE-----\n";
        var separator = payload.IndexOf("\n\n", StringComparison.Ordinal);
        var text = payload[..(separator + 1)] + "gpgsig " + signature.TrimEnd('\n').Replace("\n", "\n ", StringComparison.Ordinal)
            + "\n" + payload[(separator + 1)..];
        var oid = ObjectOid("commit", Encoding.UTF8.GetBytes(text));
        var date = ServerNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Commits[oid] = new JsonObject
        {
            ["sha"] = oid,
            ["tree"] = new JsonObject { ["sha"] = tree },
            ["parents"] = new JsonArray(new JsonObject { ["sha"] = before }),
            ["message"] = message,
            ["author"] = new JsonObject { ["name"] = "github-actions[bot]", ["email"] = "41898282+github-actions[bot]@users.noreply.github.com", ["date"] = date },
            ["committer"] = new JsonObject { ["name"] = "GitHub", ["email"] = "noreply@github.com", ["date"] = date },
            ["checkpointAuthor"] = new JsonObject { ["__typename"] = "User", ["id"] = "MDM6Qm90NDE4OTgyODI=", ["databaseId"] = 41898282, ["login"] = "github-actions[bot]" },
            ["checkpointSignature"] = new JsonObject
            {
                ["__typename"] = "GpgSignature",
                ["isValid"] = true,
                ["state"] = "VALID",
                ["wasSignedByGitHub"] = true,
                ["payload"] = payload,
                ["signature"] = signature,
            },
        };
        Refs[reference] = oid;
        SuccessfulCas++;
        return (200, new { data = new { createCommitOnBranch = new { clientMutationId = input["clientMutationId"]!.GetValue<string>() } } });
    }

    private JsonObject CheckpointSnapshot(JsonObject variables)
    {
        Assert.Equal("Owner", variables["owner"]!.GetValue<string>());
        Assert.Equal("repo", variables["name"]!.GetValue<string>());
        var historical = variables["anchor"] is not null;
        var coordination = (variables[historical ? "anchor" : "coord"]!).GetValue<string>();
        if (!historical) Assert.StartsWith("refs/heads/contract-scribe/coordination/", coordination);
        var repository = new JsonObject
        {
            ["id"] = "REPO_node",
            ["fullDatabaseId"] = "1",
            ["name"] = "repo",
            ["owner"] = new JsonObject { ["login"] = "Owner" },
            ["base"] = historical ? null : Reference(variables["target"]!.GetValue<string>(), includeContent: false),
            ["coord"] = Reference(historical ? variables["coord"]!.GetValue<string>() : coordination, includeContent: !historical),
        };
        if (historical) repository["document"] = Reference(coordination, includeContent: true)?["target"]!.DeepClone();
        foreach (var (alias, path) in new[] { ("state", "coordination-state-v1.json"), ("checkpoint", "checkpoint.json"),
            ("plan", "plan.json"), ("archive", "archive-predecessor.json"), ("event", "publication-event.json") })
        {
            if (historical && alias != "state") continue;
            Assert.Equal(coordination + ":.contract-scribe/" + path, variables[alias]!.GetValue<string>());
            repository[alias] = File(path);
        }
        return new JsonObject { ["data"] = new JsonObject { ["repository"] = repository } };

        JsonObject? Reference(string name, bool includeContent)
        {
            var oid = name;
            if (!historical || !Commits.ContainsKey(name))
                if (!Refs.TryGetValue(name, out oid)) return null;
            var target = new JsonObject { ["__typename"] = "Commit", ["oid"] = oid };
            if (includeContent)
            {
                var raw = Commits[oid];
                var author = raw["author"]!.DeepClone();
                author["user"] = raw["checkpointAuthor"]?.DeepClone();
                target["message"] = raw["message"]!.DeepClone();
                target["author"] = author;
                target["committer"] = raw["committer"]!.DeepClone();
                var parents = raw["parents"]!.AsArray();
                target["parents"] = new JsonObject
                {
                    ["totalCount"] = parents.Count,
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = parents.Count > 2 },
                    ["nodes"] = new JsonArray(parents.Take(2).Select(parent => (JsonNode)new JsonObject { ["oid"] = parent!["sha"]!.DeepClone() }).ToArray()),
                };
                target["signature"] = raw["checkpointSignature"]?.DeepClone();
                target["tree"] = Tree(raw["tree"]!["sha"]!.GetValue<string>(), includeChildren: true);
            }
            return new JsonObject { ["id"] = "REF_node", ["prefix"] = "refs/heads/", ["name"] = name[11..], ["target"] = target };
        }

        JsonObject Tree(string oid, bool includeChildren)
        {
            var entries = new JsonArray();
            foreach (var entry in Trees[oid])
            {
                var value = new JsonObject { ["name"] = entry.Path, ["mode"] = Convert.ToInt32(entry.Mode, 8), ["oid"] = entry.Oid };
                if (includeChildren) value["object"] = entry.Mode is "040000" or "40000"
                    ? Tree(entry.Oid, includeChildren: false) : new JsonObject { ["__typename"] = "Blob" };
                entries.Add(value);
            }
            return new JsonObject { ["__typename"] = "Tree", ["oid"] = oid, ["entries"] = entries };
        }

        JsonObject? File(string path)
        {
            var head = coordination;
            if (!historical && !Refs.TryGetValue(coordination, out head)) return null;
            var root = Trees[Commits[head]["tree"]!["sha"]!.GetValue<string>()];
            var directory = root.SingleOrDefault(entry => entry.Path == ".contract-scribe" && entry.Mode is "040000" or "40000");
            if (directory is null) return null;
            var entry = Trees[directory.Oid].SingleOrDefault(item => item.Path == path);
            if (entry is null || !Blobs.TryGetValue(entry.Oid, out var bytes)) return null;
            return new JsonObject
            {
                ["__typename"] = "Blob",
                ["oid"] = entry.Oid,
                ["byteSize"] = bytes.Length,
                ["isBinary"] = false,
                ["isTruncated"] = false,
                ["text"] = Encoding.UTF8.GetString(bytes),
            };
        }
    }

    private (int, object) Read(string path)
    {
        if (path == "/repos/Owner/repo") return (200, Repository());
        if (path.StartsWith("/repos/Owner/repo/pulls", StringComparison.Ordinal))
        {
            foreach (var pr in PullRequests)
            {
                if (Refs.TryGetValue("refs/heads/" + pr["head"]!["ref"]!.GetValue<string>(), out var headOid))
                    pr["head"]!["sha"] = headOid;
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
        if (path.Contains("/git/commits/", StringComparison.Ordinal) && Commits.TryGetValue(key, out var commit)) return (200, CommitResponse(commit));
        return Missing();
    }

    private static JsonObject CommitResponse(JsonObject raw)
    {
        var response = raw.DeepClone().AsObject();
        var message = response["message"]!.GetValue<string>();
        // GitHub projects the message field; retain the raw object and its independently hashed OID.
        if (message.EndsWith('\n')) response["message"] = message[..^1];
        return response;
    }

    internal int MaximumRequests { get; set; } = 20_000;

    internal void Lifecycle(string kind, int index = 0)
    {
        lock (Gate)
        {
            var pr = PullRequests[index];
            var closed = kind is "closed" or "merged";
            pr["state"] = closed ? "closed" : "open"; pr["draft"] = kind == "draft";
            pr["merged"] = kind == "merged";
            pr["closed_at"] = closed ? "2026-01-02T00:00:00Z" : null;
            pr["merged_at"] = kind == "merged" ? "2026-01-02T00:00:00Z" : null;
        }
    }

    internal JsonObject CoordinationDocument() => DocumentAt(
        Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal)).Value);

    internal JsonObject DocumentAt(string head)
    {
        var root = Trees[Commits[head]["tree"]!["sha"]!.GetValue<string>()];
        var leaf = Trees[root.Single(entry => entry.Path == ".contract-scribe").Oid];
        return JsonNode.Parse(Blobs[leaf.Single(entry => entry.Path == "coordination-state-v1.json").Oid])!.AsObject();
    }

    internal JsonObject Coordination() => JsonNode.Parse(Blobs[
        CoordinationDocument()["publication"]!["oid"]!.GetValue<string>()])!["state"]!.DeepClone().AsObject();

    internal byte[] CheckpointBytes() => Blobs[CoordinationDocument()["checkpoint"]!["oid"]!.GetValue<string>()].ToArray();

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
    private static (int, object) InvalidGraph() => (422, new { message = "Invalid synthetic Git object graph" });
    private static JsonObject Actor() => new()
    {
        ["name"] = "Synthetic",
        ["email"] = "synthetic@example.invalid",
        ["date"] = "2000-01-01T00:00:00Z",
    };
    internal string BuildTree(IReadOnlyDictionary<string, byte[]> files) => AddTree(files.GroupBy(p => p.Key.Split('/')[0])
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
