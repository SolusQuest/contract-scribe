using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    [Theory]
    [InlineData("success", true)]
    [InlineData("unsigned", false)]
    [InlineData("lost-before", false)]
    [InlineData("lost-after", true)]
    [InlineData("ack-mismatch", true)]
    [InlineData("concurrent-head", false)]
    [InlineData("stale-ref", false)]
    [InlineData("wrong-actor", false)]
    [InlineData("wrong-parent", false)]
    [InlineData("wrong-repo", false)]
    [InlineData("wrong-state", false)]
    [InlineData("wrong-metadata", false)]
    [InlineData("wrong-signature", false)]
    [InlineData("final-ref", false)]
    [InlineData("cancel-after", false)]
    [InlineData("partial-errors", false)]
    [InlineData("null-text", false)]
    [InlineData("truncated", false)]
    [InlineData("oid-alias", false)]
    [InlineData("parent-page", false)]
    [InlineData("wrong-principal", false)]
    [InlineData("null-principal", false)]
    [InlineData("wrong-ref-id", false)]
    [InlineData("owner-change", false)]
    [InlineData("different-principal", true)]
    [InlineData("cancel-read", false)]
    public Task Aggregated_checkpoint_candidate_preserves_all_bytes_and_fresh_fence(string fault, bool confirmed) =>
        RunAtomicCandidate(fault, confirmed);

    private async Task RunAtomicCandidate(string fault, bool confirmed)
    {
        var remote = new CoordinationRemote();
        using var stop = new CancellationTokenSource();
        using var handler = new AtomicCheckpointHandler(remote, fault, stop);
        using var client = CheckpointClient(remote, handler: handler);
        await using var store = GitHubCoordinationStore.Create(client);
        Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
        var before = remote.CoordinationHead!;
        var document = FixtureDocuments[before].RetainAt(before) with { PredecessorOid = before };
        document = document with { Claim = document.Claim! with { ExpiresUnixSeconds = document.Claim.ExpiresUnixSeconds + 1 } };
        var prepared = GitHubCoordinationObjects.Prepare(document);
        var write = new GitHubCheckpointWrite(prepared, []);
        client.BindCheckpointWrite(write);
        var predecessor = await client.GetRefAsync(GitHubPublicationFactory.CreateCoordinationRef(client.ReadAuthority));
        Assert.Equal(before, predecessor.Value!.Oid);
        var operation = new GitHubAtomicCheckpointCommit(write, client.AuthenticatedRepository!,
            predecessor.Value, fault == "different-principal"
                ? new(99, "NODE_OTHER", "review-user", GitHubActorKind.User) : ContractScribe.GitHub.PullRequests.GitHubPublicationPrincipal.ActionsBot);
        handler.Operation = operation;
        var result = await client.CommitCheckpointAsync(operation, stop.Token);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(operation.Context, result.Context);
        Assert.NotEqual(GitHubDelivery.NotDispatched, result.Delivery);
        GitHubCommit? observed;
        if (fault is "cancel-after" or "cancel-read")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Observe(stop.Token).AsTask());
            observed = null;
        }
        else observed = await Observe(default);
        Assert.Equal(confirmed, observed is not null);
        Assert.False(client.HasMutationAuthority);
        if (observed is not null)
        {
            Assert.NotEqual(prepared.CommitOid, observed.Oid); // Actual web metadata/signature, not the old fixed actor/date.
            Assert.Equal(prepared.RootTreeOid, observed.TreeOid);
            Assert.Equal(before, Assert.Single(observed.Parents));
            Assert.Equal(3, handler.Requests); // Mutation + complete physical query + independent final ref.
            Assert.True(client.ServerTime < observed.Author.Date.AddYears(-1)); // Commit metadata never becomes the lease clock.
            Assert.False(store.CheckpointToken.IsCancellationRequested);
        }
        Console.WriteLine($"atomic={fault}; ownerAndPredecessorSetupHTTP={handler.SetupRequests}; mutationAndReadbackHTTP={handler.Requests}; totalHTTP={handler.SetupRequests + handler.Requests}");
        var replay = await client.CommitCheckpointAsync(operation, default);
        Assert.Equal(GitHubFailureCode.InvalidRequest, replay.Failure!.Code);
        Assert.Equal(GitHubDelivery.NotDispatched, replay.Delivery);
        Assert.Equal(1, handler.Posts);
        if (fault == "stale-ref")
        {
            Assert.NotNull(await Observe(default));
            Assert.Equal(1, handler.Posts);
        }
        client.BindCheckpointWrite(null);
        handler.Operation = null;

        ValueTask<GitHubCommit?> Observe(CancellationToken token) => operation.ObserveAggregatedAsync(client, token);
    }

    private sealed class AtomicCheckpointHandler(CoordinationRemote remote, string fault, CancellationTokenSource stop) : HttpMessageHandler
    {
        internal GitHubAtomicCheckpointCommit? Operation { get; set; }
        internal int Posts, Requests, SetupRequests;
        private JsonObject? actualCommit;
        private bool oldRef;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (Operation is not { } operation) { SetupRequests++; return await Base(request); }
            Requests++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/graphql", path);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = body.RootElement;
                if (root.GetProperty("query").GetString() == GitHubApiClient.CheckpointBundleDocument)
                {
                    var bundle = await Bundle(operation, token);
                    if (fault == "cancel-read") stop.Cancel();
                    if (fault == "partial-errors") return Reply(new { data = bundle["data"], errors = new[] { new { message = "synthetic partial" } } });
                    if (fault is "final-ref" or "owner-change") MoveHead(operation);
                    return Reply(bundle);
                }
                Posts++;
                Assert.Equal(GitHubApiClient.CommitCheckpointDocument, root.GetProperty("query").GetString());
                var input = root.GetProperty("variables").GetProperty("input");
                Assert.Equal(operation.Context.BeforeOid, input.GetProperty("expectedHeadOid").GetString());
                Assert.Equal(operation.Context.RefNodeId, input.GetProperty("branch").GetProperty("id").GetString());
                Assert.Single(input.GetProperty("branch").EnumerateObject());
                var message = input.GetProperty("message");
                Assert.Equal(operation.Context.Message, message.GetProperty("headline").GetString() + "\n\n" + message.GetProperty("body").GetString());
                var files = input.GetProperty("fileChanges").GetProperty("additions").EnumerateArray().ToArray();
                Assert.Equal(operation.Files.Length, files.Length);
                foreach (var file in files)
                {
                    var expected = operation.Files.Single(pair => pair.Key == file.GetProperty("path").GetString());
                    Assert.Equal(expected.Value.ToArray(), Convert.FromBase64String(file.GetProperty("contents").GetString()!));
                }
                if (fault == "lost-before") throw new HttpRequestException("Synthetic before-effect loss");
                if (fault == "concurrent-head") remote.ForceCoordinationHead(Oid('9'));
                if (remote.CoordinationHead != operation.Context.BeforeOid)
                    return Reply(new { errors = new[] { new { type = "CONFLICT" } } });
                remote.SeedObjects(operation.Write.Prepared);
                actualCommit = BuildCommit(operation);
                remote.ForceCoordinationHead(actualCommit["sha"]!.GetValue<string>());
                if (fault == "wrong-state") remote.CorruptBlob(operation.Write.Prepared.BlobOid);
                if (fault == "stale-ref") oldRef = true;
                if (fault == "cancel-after") { stop.Cancel(); throw new OperationCanceledException(token); }
                if (fault == "lost-after") throw new HttpRequestException("Synthetic after-effect loss");
                return Reply(new { data = new { createCommitOnBranch = new { clientMutationId = fault == "ack-mismatch" ? "wrong" : operation.Context.ClientMutationId } } });
            }
            if (actualCommit is not null && path.EndsWith("/git/commits/" + actualCommit["sha"]!.GetValue<string>(), StringComparison.Ordinal))
            {
                if (fault == "cancel-read") stop.Cancel();
                if (fault is "final-ref" or "owner-change") MoveHead(operation);
                return Reply(actualCommit);
            }
            if (oldRef && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                oldRef = false;
                return Reply(new { @ref = operation.Context.Ref, node_id = "REF_SYNTHETIC", @object = new { type = "commit", sha = operation.Context.BeforeOid } });
            }
            var response = await Base(request);
            if (fault == "wrong-ref-id" && path.Contains("/git/ref/heads/contract-scribe/", StringComparison.Ordinal))
            {
                var reference = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                reference["node_id"] = "REF_OTHER_REPOSITORY";
                response.Dispose();
                return Reply(reference);
            }
            if (fault == "wrong-repo" && path == "/repos/Owner/repo")
            {
                var repository = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                repository["id"] = 999;
                response.Dispose();
                return Reply(repository);
            }
            return response;
        }

        private async Task<JsonObject> Bundle(GitHubAtomicCheckpointCommit operation, CancellationToken token)
        {
            var repository = await Read("/repos/Owner/repo");
            var reference = await Read("/repos/Owner/repo/git/ref/" + operation.Context.Ref[5..]);
            var before = await Read("/repos/Owner/repo/git/ref/heads/main");
            if (actualCommit is null) return new JsonObject { ["data"] = new JsonObject { ["repository"] = null } };
            var root = await Read("/repos/Owner/repo/git/trees/" + actualCommit["tree"]!["sha"]!.GetValue<string>());
            var rootEntry = root["tree"]!.AsArray().Single()!;
            var leaf = await Read("/repos/Owner/repo/git/trees/" + rootEntry["sha"]!.GetValue<string>());
            var entries = new JsonArray();
            var payloads = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var entry in leaf["tree"]!.AsArray())
            {
                var blob = await Read("/repos/Owner/repo/git/blobs/" + entry!["sha"]!.GetValue<string>());
                var bytes = Convert.FromBase64String(blob["content"]!.GetValue<string>());
                entries.Add(new JsonObject
                {
                    ["name"] = entry["path"]!.DeepClone(),
                    ["mode"] = Convert.ToInt32(entry["mode"]!.GetValue<string>(), 8),
                    ["oid"] = entry["sha"]!.DeepClone(),
                });
                payloads.Add(entry["path"]!.GetValue<string>(), new JsonObject
                {
                    ["__typename"] = "Blob",
                    ["oid"] = blob["sha"]!.DeepClone(),
                    ["byteSize"] = bytes.Length,
                    ["isBinary"] = false,
                    ["isTruncated"] = fault == "truncated",
                    ["text"] = fault == "null-text" ? null : Encoding.UTF8.GetString(bytes),
                });
            }
            if (fault == "oid-alias") payloads.Values.First()["oid"] = Oid('9');
            var signature = actualCommit["verification"]!;
            var author = actualCommit["author"]!.DeepClone();
            author["user"] = fault == "null-principal" ? null : new JsonObject
            {
                ["__typename"] = "User",
                ["id"] = operation.ExpectedPrincipal.NodeId,
                ["login"] = operation.ExpectedPrincipal.Login,
                ["databaseId"] = fault is "wrong-principal" or "wrong-actor" ? 999 : operation.ExpectedPrincipal.Id,
            };
            var commit = new JsonObject
            {
                ["__typename"] = "Commit",
                ["oid"] = oldRef ? operation.Context.BeforeOid : reference["object"]!["sha"]!.GetValue<string>(),
                ["message"] = actualCommit["message"]!.DeepClone(),
                ["author"] = author,
                ["committer"] = actualCommit["committer"]!.DeepClone(),
                ["parents"] = new JsonObject
                {
                    ["totalCount"] = 1,
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = fault == "parent-page" },
                    ["nodes"] = new JsonArray(new JsonObject { ["oid"] = actualCommit["parents"]![0]!["sha"]!.DeepClone() }),
                },
                ["signature"] = fault == "unsigned" ? null : new JsonObject
                {
                    ["__typename"] = "GpgSignature",
                    ["isValid"] = true,
                    ["state"] = "VALID",
                    ["wasSignedByGitHub"] = true,
                    ["payload"] = signature["payload"]!.DeepClone(),
                    ["signature"] = signature["signature"]!.DeepClone(),
                },
                ["tree"] = new JsonObject
                {
                    ["oid"] = root["sha"]!.DeepClone(),
                    ["entries"] = new JsonArray(new JsonObject
                    {
                        ["name"] = rootEntry["path"]!.DeepClone(),
                        ["mode"] = Convert.ToInt32(rootEntry["mode"]!.GetValue<string>(), 8),
                        ["oid"] = rootEntry["sha"]!.DeepClone(),
                        ["object"] = new JsonObject { ["__typename"] = "Tree", ["oid"] = leaf["sha"]!.DeepClone(), ["entries"] = entries },
                    }),
                },
            };
            oldRef = false;
            var coord = Ref(reference, commit);
            if (fault == "wrong-ref-id") coord["id"] = "REF_OTHER_REPOSITORY";
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["repository"] = new JsonObject
                    {
                        ["id"] = repository["node_id"]!.DeepClone(),
                        ["fullDatabaseId"] = (fault == "wrong-repo" ? 999 : repository["id"]!.GetValue<long>()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["name"] = repository["name"]!.DeepClone(),
                        ["owner"] = new JsonObject { ["login"] = repository["owner"]!["login"]!.DeepClone() },
                        ["base"] = Ref(before, new JsonObject { ["__typename"] = "Commit", ["oid"] = before["object"]!["sha"]!.DeepClone() }),
                        ["coord"] = coord,
                        ["state"] = Payload("coordination-state-v1.json"),
                        ["checkpoint"] = Payload("checkpoint.json"),
                        ["plan"] = Payload("plan.json"),
                        ["archive"] = Payload("archive-predecessor.json"),
                        ["event"] = Payload("publication-event.json"),
                    },
                },
            };

            JsonObject? Payload(string path) => payloads.TryGetValue(path, out var item) ? item : null;

            async Task<JsonObject> Read(string path)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:18765" + path);
                using var response = await remote.Reply(request);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
            }
            static JsonObject Ref(JsonObject value, JsonObject target) => new()
            {
                ["id"] = value["node_id"]!.DeepClone(),
                ["name"] = value["ref"]!.GetValue<string>()[11..],
                ["prefix"] = "refs/heads/",
                ["target"] = target,
            };
        }

        private void MoveHead(GitHubAtomicCheckpointCommit operation)
        {
            if (fault != "owner-change") { remote.ForceCoordinationHead(Oid('9')); return; }
            var head = remote.CoordinationHead!;
            var document = operation.Write.Prepared.Document.RetainAt(head) with
            {
                PredecessorOid = head,
                Claim = operation.Write.Prepared.Document.Claim! with
                {
                    Owner = new string('f', 64),
                    Epoch = operation.Write.Prepared.Document.Claim.Epoch + 1,
                },
            };
            var foreign = GitHubCoordinationObjects.Prepare(document);
            remote.SeedObjects(foreign);
            remote.ForceCoordinationHead(foreign.CommitOid);
        }

        private JsonObject BuildCommit(GitHubAtomicCheckpointCommit operation)
        {
            const string date = "2030-01-02T03:04:05Z";
            var seconds = DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();
            var author = fault == "wrong-actor" ? "other-user" : fault == "different-principal" ? "Review User" : "github-actions[bot]";
            var email = fault == "different-principal" ? "review.user@example.invalid" : "41898282+github-actions[bot]@users.noreply.github.com";
            var parent = fault == "wrong-parent" ? Oid('9') : operation.Context.BeforeOid;
            var prefix = "tree " + operation.Context.TreeOid + "\nparent " + parent
                + "\nauthor " + author + " <" + email + "> " + seconds + " +0000\n"
                + "committer GitHub <noreply@github.com> " + seconds + " +0000\n";
            const string signature = "-----BEGIN PGP SIGNATURE-----\n\nc3ludGhldGlj\n-----END PGP SIGNATURE-----\n";
            var payload = prefix + "\n" + operation.Context.Message;
            // Independent literal Git signed-object layout; no production
            // serializer or authenticity helper constructs these bytes.
            var raw = fault == "unsigned" ? payload : prefix
                + "gpgsig -----BEGIN PGP SIGNATURE-----\n \n c3ludGhldGlj\n -----END PGP SIGNATURE-----\n\n" + operation.Context.Message;
            var oid = GitObjectOid("commit", Encoding.UTF8.GetBytes(raw));
            return new JsonObject
            {
                ["sha"] = oid,
                ["tree"] = new JsonObject { ["sha"] = operation.Context.TreeOid },
                ["parents"] = new JsonArray(new JsonObject { ["sha"] = parent }),
                ["message"] = operation.Context.Message,
                ["author"] = new JsonObject { ["name"] = fault == "wrong-metadata" ? "wire-substitution" : author, ["email"] = email, ["date"] = date },
                ["committer"] = new JsonObject { ["name"] = "GitHub", ["email"] = "noreply@github.com", ["date"] = date },
                ["verification"] = new JsonObject
                {
                    ["verified"] = fault != "unsigned",
                    ["reason"] = fault == "unsigned" ? "unsigned" : "valid",
                    ["signature"] = fault == "unsigned" ? null : fault == "wrong-signature" ? signature.Replace("c3ludGhldGlj", "Y2hhbmdlZA==", StringComparison.Ordinal) : signature,
                    ["payload"] = fault == "unsigned" ? null : payload,
                },
            };
        }

        private async Task<HttpResponseMessage> Base(HttpRequestMessage request)
        {
            var response = await remote.Reply(request);
            response.Headers.Date = remote.ServerNow;
            return response;
        }

        private HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            Headers = { Date = remote.ServerNow },
        };
    }
}
