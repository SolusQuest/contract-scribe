using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContractScribe.GitHub.Transport;

internal static class GitHubResponseReader
{
    internal const int MaximumBlobBytes = 16 * 1024 * 1024;
    internal const int MaximumBodyBytes = 24 * 1024 * 1024;
    internal const int MaximumTreeEntries = 100_000;
    internal const int MaximumPages = 100;
    internal const int MaximumPullRequests = 10_000;
    internal const long MaximumCollectionBytes = 64 * 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static JsonDocument Parse(byte[] bytes)
    {
        Require(bytes.Length <= MaximumBodyBytes);
        _ = Utf8.GetCharCount(bytes);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            Scan(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void Scan(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Count < 256 && property.Name.Length <= 256 && names.Add(property.Name));
                _ = Utf8.GetByteCount(property.Name);
                Scan(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            Require(value.GetArrayLength() <= MaximumTreeEntries);
            foreach (var item in value.EnumerateArray()) Scan(item);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            _ = Utf8.GetByteCount(value.GetString()!);
        }
    }

    internal static GitHubRepository Repository(JsonElement value)
    {
        var identity = RepositoryIdentity(value);
        GitHubRolePermissions? roles = null;
        if (value.TryGetProperty("permissions", out var permission))
        {
            Object(permission);
            roles = new(Boolean(permission, "admin"), Boolean(permission, "push"), Boolean(permission, "pull"),
                OptionalBoolean(permission, "maintain"), OptionalBoolean(permission, "triage"));
        }
        return new(identity, Boolean(value, "private"), Boolean(value, "archived"), Boolean(value, "disabled"), roles);
    }

    internal static GitHubRepositoryIdentity RepositoryIdentity(JsonElement value)
    {
        Object(value);
        var owner = Actor(Property(value, "owner"));
        Require(owner.Kind != GitHubActorKind.Mannequin);
        var name = String(value, "name", 256);
        Require(RepositoryPart(name) && RepositoryPart(owner.Login));
        Require(String(value, "full_name", 513) == owner.Login + "/" + name);
        return new(Positive(value, "id"), Node(value, "node_id"), owner.Login, name);
    }

    internal static GitHubActor Actor(JsonElement value)
    {
        Object(value);
        var login = String(value, "login", 256);
        Require(login.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '[' or ']'));
        var kind = String(value, "type", 32) switch
        {
            "User" => GitHubActorKind.User,
            "Bot" => GitHubActorKind.Bot,
            "Organization" => GitHubActorKind.Organization,
            "Mannequin" => GitHubActorKind.Mannequin,
            _ => throw new GitHubProtocolException(),
        };
        return new(Positive(value, "id"), Node(value, "node_id"), login, kind);
    }

    internal static GitHubRef Ref(JsonElement value, string expected)
    {
        Require(String(value, "ref", 1024) == expected);
        var target = Property(value, "object");
        Require(String(target, "type", 16) == "commit");
        return new(expected, Node(value, "node_id"), Oid(target, "sha"));
    }

    internal static GitHubBlob Blob(JsonElement value, string expected)
    {
        Require(Oid(value, "sha") == expected && String(value, "encoding", 16) == "base64");
        var size = Integer(value, "size", 0, MaximumBlobBytes);
        var content = String(value, "content", MaximumBodyBytes, allowEmpty: true);
        var count = 0;
        var padding = 0;
        foreach (var c in content)
        {
            if (c is '\r' or '\n') continue;
            if (c == '=') padding++;
            else Require(padding == 0 && (char.IsAsciiLetterOrDigit(c) || c is '+' or '/'));
            count++;
        }
        Require(count % 4 == 0 && padding <= 2 && (long)count / 4 * 3 - padding == size);
        var bytes = new byte[(int)size];
        Require(Convert.TryFromBase64String(content, bytes, out var written) && written == size);
        var normalized = content.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        Require(Convert.ToBase64String(bytes) == normalized);
        return new(expected, ImmutableArray.CreateRange(bytes));
    }

    internal static GitHubTree Tree(JsonElement value, string expected)
    {
        Require(Oid(value, "sha") == expected && !Boolean(value, "truncated"));
        var entries = Property(value, "tree");
        Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() <= MaximumTreeEntries);
        var result = ImmutableArray.CreateBuilder<GitHubTreeEntry>(entries.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var path = String(entry, "path", 1024);
            Require(TreeName(path) && paths.Add(path));
            var mode = String(entry, "mode", 6) switch
            {
                "100644" => GitHubTreeMode.File,
                "100755" => GitHubTreeMode.Executable,
                "040000" => GitHubTreeMode.Directory,
                "120000" => GitHubTreeMode.SymbolicLink,
                "160000" => GitHubTreeMode.Submodule,
                _ => throw new GitHubProtocolException(),
            };
            Require(String(entry, "type", 16) == ObjectType(mode));
            long? size = entry.TryGetProperty("size", out _) ? Integer(entry, "size", 0, long.MaxValue) : null;
            result.Add(new(path, mode, Oid(entry, "sha"), size));
        }
        return new(expected, result.MoveToImmutable());
    }

    internal static GitHubCommit Commit(JsonElement value, string expected)
    {
        Require(Oid(value, "sha") == expected);
        var parents = Property(value, "parents");
        Require(parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() <= 64);
        var result = parents.EnumerateArray().Select(parent => Oid(parent, "sha")).ToImmutableArray();
        Require(result.Distinct(StringComparer.Ordinal).Count() == result.Length);
        return new(expected, Oid(Property(value, "tree"), "sha"), result,
            String(value, "message", 65536, allowEmpty: true), CommitActor(Property(value, "author")),
            CommitActor(Property(value, "committer")));
    }

    private static GitHubCommitActor CommitActor(JsonElement value) => new(
        String(value, "name", 256), String(value, "email", 320), Date(value, "date"));

    internal static GitHubPullRequest PullRequest(JsonElement value, GitHubRepositoryIdentity repository, bool detail)
    {
        var number = (int)Integer(value, "number", 1, int.MaxValue);
        var state = String(value, "state", 16);
        Require(state is "open" or "closed");
        var mergedAt = NullableDate(value, "merged_at");
        var closedAt = NullableDate(value, "closed_at");
        var merged = detail ? Boolean(value, "merged") : OptionalBoolean(value, "merged");
        Require((state == "open") == (closedAt is null));
        Require(mergedAt is null || state == "closed");
        Require(merged is null || merged.Value == (mergedAt is not null));
        var basis = Property(value, "base");
        var baseRepository = RepositoryIdentity(Property(basis, "repo"));
        Require(baseRepository == repository);
        var baseRef = String(basis, "ref", 1024);
        Require(RefName("refs/heads/" + baseRef));
        var head = Property(value, "head");
        Object(head);
        GitHubRepositoryIdentity? headRepository = null;
        var headRepo = Property(head, "repo");
        if (headRepo.ValueKind != JsonValueKind.Null) headRepository = RepositoryIdentity(headRepo);
        var headRef = NullableString(head, "ref", 1024);
        var headOid = NullableString(head, "sha", 40);
        Require(headRef is null || RefName("refs/heads/" + headRef));
        Require(headOid is null || IsOid(headOid));
        var maintainer = detail ? Boolean(value, "maintainer_can_modify") : OptionalBoolean(value, "maintainer_can_modify");
        // The list schema permits explicit null, but not an absent property or malformed actor.
        // Detail/create responses and all principal/owner positions still require an actor.
        var user = Property(value, "user");
        var author = !detail && user.ValueKind == JsonValueKind.Null ? null : Actor(user);
        return new(Positive(value, "id"), Node(value, "node_id"), number, state == "open", Boolean(value, "draft"),
            merged, mergedAt, closedAt, Date(value, "created_at"), String(value, "title", 256),
            NullableString(value, "body", 65536), author, new(headRepository, headRef, headOid),
            baseRepository, baseRef, Oid(basis, "sha"), maintainer);
    }

    internal static GitHubAcknowledgement GraphQl(JsonElement value, string expected)
    {
        Exact(value, "data", "errors");
        if (value.TryGetProperty("errors", out var errors))
        {
            Require(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() is > 0 and <= 32);
            GitHubFailureCode? code = null;
            foreach (var error in errors.EnumerateArray())
            {
                Exact(error, "message", "type", "path", "locations", "extensions");
                _ = String(error, "message", 8192);
                var itemCode = GitHubFailureCode.InvalidResponse;
                if (error.TryGetProperty("type", out _))
                {
                    itemCode = String(error, "type", 64) switch
                    {
                        "FORBIDDEN" => GitHubFailureCode.Permission,
                        "NOT_FOUND" => GitHubFailureCode.NotFound,
                        "RATE_LIMITED" => GitHubFailureCode.RateLimit,
                        "UNPROCESSABLE" => GitHubFailureCode.Validation,
                        "CONFLICT" => GitHubFailureCode.Conflict,
                        _ => GitHubFailureCode.InvalidResponse,
                    };
                }
                code = code is null || code == itemCode ? itemCode : GitHubFailureCode.InvalidResponse;
                if (error.TryGetProperty("path", out var path))
                    Require(path.ValueKind == JsonValueKind.Array && path.GetArrayLength() <= 16
                        && path.EnumerateArray().All(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number));
                if (error.TryGetProperty("locations", out var locations))
                {
                    Require(locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() <= 16);
                    foreach (var location in locations.EnumerateArray())
                    {
                        Exact(location, "line", "column");
                        _ = Integer(location, "line", 1, int.MaxValue);
                        _ = Integer(location, "column", 1, int.MaxValue);
                    }
                }
                if (error.TryGetProperty("extensions", out var extensions)) Object(extensions);
            }
            if (value.TryGetProperty("data", out var partial) && partial.ValueKind != JsonValueKind.Null)
            {
                Exact(partial, "updateRefs");
                var update = Property(partial, "updateRefs");
                if (update.ValueKind != JsonValueKind.Null)
                {
                    Exact(update, "clientMutationId");
                    _ = String(update, "clientMutationId", 128);
                }
            }
            throw new GitHubProtocolException(code ?? GitHubFailureCode.InvalidResponse);
        }
        var data = Property(value, "data");
        Exact(data, "updateRefs");
        var payload = Property(data, "updateRefs");
        Exact(payload, "clientMutationId");
        Require(String(payload, "clientMutationId", 128) == expected);
        return new();
    }

    // URLs are checked as redundant identity observations, never followed.
    internal static void GitObjectUrls(JsonElement value, Uri origin, GitHubRepositoryIdentity repository, string requestPath)
    {
        var operation = requestPath[(requestPath.IndexOf("/git/", StringComparison.Ordinal) + 5)..].Split('/')[0];
        if (operation == "ref")
        {
            var name = String(value, "ref", 1024);
            Url(value, string.Join('/', name.Split('/').Select(Uri.EscapeDataString)));
            var target = Property(value, "object");
            Url(target, "commits/" + Oid(target, "sha"));
            return;
        }
        Url(value, operation + "/" + Oid(value, "sha"));
        if (operation == "trees")
            foreach (var entry in Property(value, "tree").EnumerateArray())
            {
                var collection = String(entry, "type", 16) switch
                {
                    "blob" => "blobs",
                    "tree" => "trees",
                    "commit" => "commits",
                    _ => throw new GitHubProtocolException(),
                };
                Url(entry, collection + "/" + Oid(entry, "sha"));
            }
        if (operation == "commits")
        {
            var tree = Property(value, "tree");
            Url(tree, "trees/" + Oid(tree, "sha"));
            foreach (var parent in Property(value, "parents").EnumerateArray()) Url(parent, "commits/" + Oid(parent, "sha"));
        }

        void Url(JsonElement element, string suffix)
        {
            if (!element.TryGetProperty("url", out _)) return;
            var raw = String(element, "url", 4096);
            Require(Uri.TryCreate(raw, UriKind.Absolute, out var uri));
            Require(raw == uri!.AbsoluteUri && uri.Scheme == origin.Scheme && uri.Host == origin.Host
                && uri.Port == origin.Port && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0);
            Require(uri.AbsolutePath == "/repos/" + repository.Owner + "/" + repository.Name + "/git/" + suffix
                || uri.AbsolutePath == "/repositories/" + repository.Id.ToString(CultureInfo.InvariantCulture) + "/git/" + suffix);
        }
    }

    internal static string ObjectType(GitHubTreeMode mode) => mode switch
    {
        GitHubTreeMode.File or GitHubTreeMode.Executable or GitHubTreeMode.SymbolicLink => "blob",
        GitHubTreeMode.Directory => "tree",
        GitHubTreeMode.Submodule => "commit",
        _ => throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest),
    };

    internal static string WireMode(GitHubTreeMode mode) => mode switch
    {
        GitHubTreeMode.File => "100644",
        GitHubTreeMode.Executable => "100755",
        GitHubTreeMode.Directory => "040000",
        GitHubTreeMode.SymbolicLink => "120000",
        GitHubTreeMode.Submodule => "160000",
        _ => throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest),
    };

    internal static bool RepositoryPart(string? text) => text is { Length: > 0 and <= 256 } && text is not ("." or "..")
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool TreeName(string? text) => text is { Length: > 0 and <= 1024 } && text is not ("." or "..")
        && !text.Any(c => c is '\0' or '/') && ValidUtf16(text);
    internal static bool RefName(string? text) => text is { Length: > 11 and <= 1024 }
        && text.StartsWith("refs/heads/", StringComparison.Ordinal) && !text.Contains("..", StringComparison.Ordinal)
        && !text.EndsWith('.') && !text.Contains("@{", StringComparison.Ordinal) && !text.Any(c => c <= ' ' || c == '\u007f'
            || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') && ValidUtf16(text)
        && text.Split('/').All(part => part.Length > 0 && !part.StartsWith('.')
            && !part.EndsWith(".lock", StringComparison.Ordinal));
    internal static bool IsOid(string? text, bool zero = false) => Hex(text, 40) && (zero || text!.Any(c => c != '0'));
    internal static bool Hex(string? text, int length) => text?.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidUtf16(string text)
    {
        try { _ = Utf8.GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static void Require(bool condition, GitHubFailureCode code = GitHubFailureCode.InvalidResponse)
    {
        if (!condition) throw new GitHubProtocolException(code);
    }
    internal static void Object(JsonElement value) => Require(value.ValueKind == JsonValueKind.Object);
    internal static void Exact(JsonElement value, params string[] names)
    {
        Object(value);
        Require(value.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal)));
    }
    internal static JsonElement Property(JsonElement value, string name)
    {
        Object(value);
        Require(value.TryGetProperty(name, out var found));
        return found;
    }
    internal static string String(JsonElement value, string name, int maximum, bool allowEmpty = false)
    {
        var property = Property(value, name);
        Require(property.ValueKind == JsonValueKind.String);
        var text = property.GetString()!;
        Require(text.Length <= maximum && (allowEmpty || text.Length > 0));
        return text;
    }
    internal static string? NullableString(JsonElement value, string name, int maximum) =>
        Property(value, name).ValueKind == JsonValueKind.Null ? null : String(value, name, maximum, allowEmpty: true);
    internal static bool Boolean(JsonElement value, string name)
    {
        var property = Property(value, name);
        Require(property.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return property.GetBoolean();
    }
    internal static bool? OptionalBoolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out _) ? Boolean(value, name) : null;
    internal static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        var property = Property(value, name);
        Require(property.ValueKind == JsonValueKind.Number);
        Require(property.TryGetInt64(out var number) && number >= minimum && number <= maximum);
        Require(property.GetRawText() == number.ToString(CultureInfo.InvariantCulture));
        return number;
    }
    internal static long Positive(JsonElement value, string name) => Integer(value, name, 1, long.MaxValue);
    internal static string Node(JsonElement value, string name)
    {
        var text = String(value, name, 256);
        Require(text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '/' or '='));
        return text;
    }
    internal static string Oid(JsonElement value, string name)
    {
        var text = String(value, name, 40);
        Require(IsOid(text));
        return text;
    }
    internal static DateTimeOffset Date(JsonElement value, string name)
    {
        var text = String(value, name, 40);
        Require(DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date));
        return date;
    }
    internal static DateTimeOffset? NullableDate(JsonElement value, string name) =>
        Property(value, name).ValueKind == JsonValueKind.Null ? null : Date(value, name);
}
