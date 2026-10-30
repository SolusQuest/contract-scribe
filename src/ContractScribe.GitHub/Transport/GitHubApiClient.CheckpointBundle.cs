using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

internal sealed record GitHubCheckpointAuthor(string NodeId, string Login, long? DatabaseId) : GitHubValue;
internal sealed record GitHubCheckpointSnapshot(GitHubRepositoryIdentity Repository, GitHubRef? Reference,
    GitHubRef? Target, GitHubCommit? Commit, GitHubTree? Root, GitHubTree? Leaf,
    ImmutableDictionary<string, GitHubBlob> Blobs, GitHubCheckpointAuthor? Author, bool SignedByGitHub) : GitHubValue;
internal sealed record GitHubCheckpointBundle(GitHubRef Reference, GitHubRef Target, GitHubCommit Commit,
    ImmutableDictionary<string, GitHubBlob> Blobs) : GitHubValue;

internal sealed partial class GitHubApiClient
{
    private string? checkpointRefNodeId;
    internal string? CheckpointRefNodeId => checkpointRefNodeId;
    private void ObserveCheckpointRefIdentity(GitHubRef reference)
    {
        lock (identityGate)
        {
            Require(checkpointRefNodeId is null || checkpointRefNodeId == reference.NodeId);
            checkpointRefNodeId = reference.NodeId;
        }
    }
    // A physical read, not a server snapshot. Only five canonical file paths
    // supply bytes; arbitrary tree entries supply metadata, never contents.
    internal const string CheckpointBundleDocument = """
        query($owner:String!,$name:String!,$coord:String!,$target:String!,$state:String!,$checkpoint:String!,$plan:String!,$archive:String!,$event:String!){
          repository(owner:$owner,name:$name){id fullDatabaseId name owner{login}
            base:ref(qualifiedName:$target){id name prefix target{__typename oid}}
            coord:ref(qualifiedName:$coord){id name prefix target{__typename oid ... on Commit{
              message author{name email date user{__typename id databaseId login}} committer{name email date}
              parents(first:2){totalCount pageInfo{hasNextPage} nodes{oid}}
              signature{__typename isValid state payload signature wasSignedByGitHub}
              tree{oid entries{name mode oid object{__typename ... on Tree{oid entries{name mode oid}}}}}
            }}}
            state:object(expression:$state){...CheckpointBlob}
            checkpoint:object(expression:$checkpoint){...CheckpointBlob}
            plan:object(expression:$plan){...CheckpointBlob}
            archive:object(expression:$archive){...CheckpointBlob}
            event:object(expression:$event){...CheckpointBlob}
          }
        }
        fragment CheckpointBlob on GitObject{__typename oid ... on Blob{byteSize isBinary isTruncated text}}
        """;

    private static readonly (string Alias, string Path)[] CheckpointFiles =
    [
        ("state", GitHubCoordinationObjects.StatePath), ("checkpoint", GitHubCoordinationObjects.CheckpointPath),
        ("plan", GitHubCoordinationObjects.PlanPath), ("archive", GitHubCoordinationObjects.ArchivePath),
        ("event", GitHubCoordinationObjects.EventPath),
    ];

    // Historical reads have one already-known commit OID and one fixed state
    // path. They never enumerate arbitrary contents or carry ref authority.
    private const string CheckpointDocumentQuery = """
        query($owner:String!,$name:String!,$coord:String!,$anchor:String!,$state:String!){
          repository(owner:$owner,name:$name){id fullDatabaseId name owner{login}
            coord:ref(qualifiedName:$coord){id name prefix target{__typename oid}}
            document:object(expression:$anchor){__typename oid ... on Commit{
              message author{name email date user{__typename id databaseId login}} committer{name email date}
              parents(first:2){totalCount pageInfo{hasNextPage} nodes{oid}}
              signature{__typename isValid state payload signature wasSignedByGitHub}
              tree{oid entries{name mode oid object{__typename ... on Tree{oid entries{name mode oid}}}}}
            }}
            state:object(expression:$state){__typename oid ... on Blob{byteSize isBinary isTruncated text}}
          }
        }
        """;

    internal ValueTask<GitHubApiResult<GitHubCheckpointSnapshot>> ReadCheckpointDocumentAsync(string oid, CancellationToken token) =>
        RunAsync<GitHubCheckpointSnapshot>(() =>
        {
            Require(IsOid(oid));
            var body = Encode(writer =>
            {
                writer.WriteString("query", CheckpointDocumentQuery);
                writer.WriteStartObject("variables");
                writer.WriteString("owner", readAuthority.RepositoryOwner);
                writer.WriteString("name", readAuthority.RepositoryName);
                writer.WriteString("coord", GitHubPublicationFactory.CreateCoordinationRef(readAuthority));
                writer.WriteString("anchor", oid);
                writer.WriteString("state", oid + ":" + GitHubCoordinationObjects.RootPath + "/" + GitHubCoordinationObjects.StatePath);
                writer.WriteEndObject();
            });
            return new("graphql", value => ReadSnapshot(value, GitHubPublicationFactory.CreateCoordinationRef(readAuthority), oid), body, Status: 200);
        }, token);

    internal ValueTask<GitHubApiResult<GitHubCheckpointSnapshot>> ReadCheckpointSnapshotAsync(CancellationToken token) =>
        RunAsync<GitHubCheckpointSnapshot>(() =>
        {
            var coordination = GitHubPublicationFactory.CreateCoordinationRef(readAuthority);
            var body = Encode(writer =>
            {
                writer.WriteString("query", CheckpointBundleDocument);
                writer.WriteStartObject("variables");
                writer.WriteString("owner", readAuthority.RepositoryOwner);
                writer.WriteString("name", readAuthority.RepositoryName);
                writer.WriteString("coord", coordination);
                writer.WriteString("target", readAuthority.TargetRef);
                foreach (var (alias, path) in CheckpointFiles)
                    writer.WriteString(alias, coordination + ":" + GitHubCoordinationObjects.RootPath + "/" + path);
                writer.WriteEndObject();
            });
            return new("graphql", value => ReadSnapshot(value, coordination), body, Status: 200);
        }, token);

    internal async ValueTask<GitHubApiResult<GitHubCheckpointBundle>> ReadCheckpointBundleAsync(
        GitHubAtomicCheckpointCommit operation, CancellationToken token)
    {
        if (operation.Context.Repository != AuthenticatedRepository
            || operation.Context.Ref != GitHubPublicationFactory.CreateCoordinationRef(readAuthority))
            return GitHubApiResult<GitHubCheckpointBundle>.Failed(GitHubFailureCode.InvalidRequest);
        var read = await ReadCheckpointSnapshotAsync(token).ConfigureAwait(false);
        if (read.Value is not { } snapshot)
            return new(null, read.Failure, read.Delivery, read.Context, read.RequiredPermissions);
        var expected = operation.ExpectedPrincipal;
        if (snapshot.Reference is not { } reference || snapshot.Target is not { } target || snapshot.Commit is not { } commit
            || reference.NodeId != operation.Context.RefNodeId || snapshot.Repository != operation.Context.Repository
            || snapshot.Author is not { } author || author.NodeId != expected.NodeId || author.Login != expected.Login
            || author.DatabaseId is { } id && id != expected.Id || !snapshot.SignedByGitHub
            || !operation.MatchesObservedCommit(commit) || snapshot.Root is not { } root || snapshot.Leaf is not { } leaf
            || root.Oid != operation.Context.TreeOid || root.Entries.Length != 1
            || root.Entries[0].Path != GitHubCoordinationObjects.RootPath || root.Entries[0].Mode != GitHubTreeMode.Directory
            || root.Entries[0].Oid != operation.Write.Prepared.LeafTreeOid || leaf.Oid != operation.Write.Prepared.LeafTreeOid)
            return GitHubApiResult<GitHubCheckpointBundle>.Failed(GitHubFailureCode.InvalidResponse, dispatched: true);
        var entries = GitHubCoordinationObjects.LeafEntries(operation.Write.Prepared);
        if (leaf.Entries.Length != entries.Length || entries.Any(entry => !leaf.Entries.Any(actual => actual.Path == entry.Path
                && actual.Mode == entry.Mode && actual.Oid == entry.Oid)
            || !snapshot.Blobs.TryGetValue(entry.Path, out var blob) || blob.Oid != entry.Oid
            || GitHubCoordinationObjects.ObjectOid("blob", blob.Bytes.AsSpan()) != entry.Oid
            || operation.Write.Blobs.TryGetValue(entry.Oid, out var bytes) && !blob.Bytes.AsSpan().SequenceEqual(bytes.AsSpan())))
            return GitHubApiResult<GitHubCheckpointBundle>.Failed(GitHubFailureCode.InvalidResponse, dispatched: true);
        return new(new(reference, target, commit, snapshot.Blobs), null, GitHubDelivery.Read);
    }

    private GitHubCheckpointSnapshot ReadSnapshot(JsonElement value, string? coordination, string? historicalOid = null)
    {
        Exact(value, "data"); // A partial error is not a usable observation.
        var data = Property(value, "data");
        Exact(data, "repository");
        var repository = Property(data, "repository");
        var identity = ObserveRepositoryIdentity(new(BigDatabaseId(repository, "fullDatabaseId"), String(repository, "id", 256),
            String(Property(repository, "owner"), "login", 256), String(repository, "name", 256)));
        var reference = coordination is null ? null : BundleRef(Property(repository, "coord"), coordination);
        if (reference is not null) ObserveCheckpointRefIdentity(reference);
        var target = historicalOid is not null || coordination is null ? null : BundleRef(Property(repository, "base"), readAuthority.TargetRef);
        var blobs = ImmutableDictionary.CreateBuilder<string, GitHubBlob>(StringComparer.Ordinal);
        foreach (var (alias, path) in CheckpointFiles)
        {
            if (historicalOid is not null && alias != "state") continue;
            var item = Property(repository, alias);
            if (item.ValueKind == JsonValueKind.Null) continue;
            Require(String(item, "__typename", 32) == "Blob" && !Boolean(item, "isTruncated") && !Boolean(item, "isBinary"));
            var bytes = Utf8.GetBytes(String(item, "text", MaximumBlobBytes, allowEmpty: true));
            Require(Integer(item, "byteSize", 0, MaximumBlobBytes) == bytes.Length);
            blobs.Add(path, new(Oid(item, "oid"), ImmutableArray.CreateRange(bytes)));
        }
        if (historicalOid is null && reference is null)
        {
            Require(blobs.Count == 0);
            return new(identity, null, target, null, null, null, blobs.ToImmutable(), null, false);
        }
        var node = historicalOid is null ? Property(Property(repository, "coord"), "target") : Property(repository, "document");
        Require(String(node, "__typename", 32) == "Commit");
        var observedOid = Oid(node, "oid");
        Require(observedOid == (historicalOid ?? reference!.Oid));
        var parents = Property(node, "parents");
        var nodes = Property(parents, "nodes");
        Require(nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() <= 2
            && Integer(parents, "totalCount", 0, 2) == nodes.GetArrayLength()
            && !Boolean(Property(parents, "pageInfo"), "hasNextPage"));
        var author = Property(node, "author");
        var user = Property(author, "user");
        if (user.ValueKind != JsonValueKind.Null) Require(String(user, "__typename", 32) == "User");
        GitHubCheckpointAuthor? principal = user.ValueKind == JsonValueKind.Null ? null
            : new(String(user, "id", 256), String(user, "login", 256), Property(user, "databaseId").ValueKind == JsonValueKind.Null
                ? null : Positive(user, "databaseId"));
        var signature = Property(node, "signature");
        var signed = signature.ValueKind != JsonValueKind.Null;
        if (signed) Require(String(signature, "__typename", 32) == "GpgSignature" && Boolean(signature, "isValid")
            && String(signature, "state", 64) == "VALID");
        var rootNode = Property(node, "tree");
        var projection = JsonSerializer.SerializeToElement(new
        {
            sha = observedOid,
            tree = new { sha = Oid(rootNode, "oid") },
            parents = nodes.EnumerateArray().Select(parent => new { sha = Oid(parent, "oid") }).ToArray(),
            message = String(node, "message", 65536, allowEmpty: true),
            author = ActorProjection(author),
            committer = ActorProjection(Property(node, "committer")),
            verification = new
            {
                verified = signed,
                reason = signed ? "valid" : "unsigned",
                payload = signed ? String(signature, "payload", 131072) : null,
                signature = signed ? String(signature, "signature", 16384) : null,
            },
        });
        // Signed bytes include raw metadata. Unsigned canonical R3 objects are
        // authenticated by their owner against its exact precomputed preimage.
        var commit = signed ? GitHubAtomicCheckpointCommit.ReadObservedCommit(projection, observedOid) : Commit(projection, observedOid);
        if (signed && Boolean(signature, "wasSignedByGitHub") && principal is not null && reference is not null)
            commit = commit with { CheckpointSignature = new(identity, reference.NodeId, principal) };
        var root = SnapshotTree(rootNode);
        GitHubTree? leaf = null;
        var rootEntries = Property(rootNode, "entries");
        if (rootEntries.GetArrayLength() == 1)
        {
            var item = Property(rootEntries[0], "object");
            if (String(item, "__typename", 32) == "Tree") leaf = SnapshotTree(item);
        }
        if (historicalOid is null && leaf is not null)
            foreach (var (_, path) in CheckpointFiles)
            {
                var entry = leaf.Entries.SingleOrDefault(item => item.Path == path);
                // Every fixed current file was physically requested. A missing
                // alias must not be supplied by a warm historical payload.
                Require(entry is null ? !blobs.ContainsKey(path)
                    : blobs.TryGetValue(path, out var blob) && blob.Oid == entry.Oid);
            }
        return new(identity, reference, target, commit, root, leaf, blobs.ToImmutable(), principal,
            signed && Boolean(signature, "wasSignedByGitHub"));

        static object ActorProjection(JsonElement actor) => new
        {
            name = String(actor, "name", 256),
            email = String(actor, "email", 320),
            date = String(actor, "date", 64),
        };
    }

    private GitHubRepositoryIdentity ObserveRepositoryIdentity(GitHubRepositoryIdentity current)
    {
        Require(AsciiCaseEqual(current.Owner, readAuthority.RepositoryOwner) && AsciiCaseEqual(current.Name, readAuthority.RepositoryName));
        lock (identityGate) { Require(repository is null || repository == current); repository = current; }
        return current;
    }

    private static long BigDatabaseId(JsonElement value, string name)
    {
        var field = Property(value, name);
        if (field.ValueKind == JsonValueKind.Number) return Positive(value, name);
        var text = String(value, name, 20);
        Require(text.Length > 0 && text[0] != '0' && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0);
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static GitHubTree SnapshotTree(JsonElement value)
    {
        var entries = Property(value, "entries");
        Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() <= MaximumTreeEntries);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = entries.EnumerateArray().Select(entry =>
        {
            var name = String(entry, "name", 1024);
            Require(TreeName(name) && names.Add(name));
            var mode = Integer(entry, "mode", 0, int.MaxValue) switch
            {
                33188 => GitHubTreeMode.File,
                33261 => GitHubTreeMode.Executable,
                16384 => GitHubTreeMode.Directory,
                40960 => GitHubTreeMode.SymbolicLink,
                57344 => GitHubTreeMode.Submodule,
                _ => throw new GitHubProtocolException(),
            };
            return new GitHubTreeEntry(name, mode, Oid(entry, "oid"), null);
        }).ToImmutableArray();
        return new(Oid(value, "oid"), result);
    }

    private static GitHubRef? BundleRef(JsonElement value, string expected)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var target = Property(value, "target");
        Require(String(value, "prefix", 1024) + String(value, "name", 1024) == expected && String(target, "__typename", 32) == "Commit");
        return new(expected, String(value, "id", 256), Oid(target, "oid"));
    }
}
