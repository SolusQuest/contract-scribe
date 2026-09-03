using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

internal static class GitHubResponseHeaders
{
    internal static string? Single(HttpResponseHeaders headers, string name, int maximum)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        using var enumerator = values.GetEnumerator();
        Require(enumerator.MoveNext());
        var value = enumerator.Current;
        Require(value.Length is > 0 && value.Length <= maximum && !enumerator.MoveNext());
        return value;
    }

    internal static GitHubRetryObservation Retry(HttpResponseHeaders headers)
    {
        int? seconds = null;
        var retry = Single(headers, "Retry-After", 128);
        if (retry is not null)
        {
            if (long.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var delta))
            {
                Require(delta is >= 0 and <= 86400);
                seconds = (int)delta;
            }
            else
            {
                Require(DateTimeOffset.TryParseExact(retry, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var retryDate));
                var date = Single(headers, "Date", 128);
                Require(date is not null && DateTimeOffset.TryParseExact(date, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out _));
                var basis = DateTimeOffset.ParseExact(date!, "r", CultureInfo.InvariantCulture);
                var difference = (retryDate - basis).TotalSeconds;
                Require(difference is >= 0 and <= 86400);
                seconds = (int)difference;
            }
        }
        return new(seconds, Number(headers, "X-RateLimit-Reset"), Number(headers, "X-RateLimit-Remaining"));
    }

    private static long? Number(HttpResponseHeaders headers, string name)
    {
        var raw = Single(headers, name, 20);
        if (raw is null) return null;
        Require(long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0);
        return value;
    }

    internal static GitHubPermissionAlternatives? Permissions(HttpResponseHeaders headers)
    {
        var raw = Single(headers, "X-Accepted-GitHub-Permissions", 4096);
        if (raw is null) return null;
        var alternatives = raw.Split(';');
        Require(alternatives.Length <= 8);
        var result = ImmutableArray.CreateBuilder<ImmutableArray<GitHubPermissionRequirement>>();
        var unrepresented = false;
        foreach (var alternative in alternatives)
        {
            var requirements = alternative.Split(',');
            Require(requirements.Length <= 8);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var group = ImmutableArray.CreateBuilder<GitHubPermissionRequirement>();
            var compatible = true;
            foreach (var requirement in requirements)
            {
                var pair = requirement.Trim().Split('=');
                Require(pair.Length == 2 && pair[0].Length is > 0 and <= 64
                    && pair[0].All(c => c is >= 'a' and <= 'z' or '_') && names.Add(pair[0]));
                var level = pair[1] switch
                {
                    "read" => GitHubPermissionLevel.Read,
                    "write" => GitHubPermissionLevel.Write,
                    _ => throw new GitHubProtocolException(),
                };
                GitHubPermission? permission = pair[0] switch
                {
                    "metadata" => GitHubPermission.Metadata,
                    "contents" => GitHubPermission.Contents,
                    "pull_requests" => GitHubPermission.PullRequests,
                    _ => null,
                };
                if (permission is null) compatible = false;
                else group.Add(new(permission.Value, level));
            }
            if (compatible) result.Add(group.OrderBy(item => item.Permission).ToImmutableArray());
            else unrepresented = true;
        }
        return new(result.ToImmutable(), unrepresented);
    }

    internal static (int? Next, int? Last) Pagination(HttpResponseHeaders headers, Uri origin,
        GitHubRepositoryIdentity repository, int current, int? advertisedLast)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            Require(advertisedLast is null || advertisedLast == current);
            return (null, advertisedLast);
        }
        var all = new List<string>();
        var length = 0;
        foreach (var value in values)
        {
            length = checked(length + value.Length);
            Require(length <= 8192 && all.Count < 4);
            all.Add(value);
        }
        var relations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in string.Join(",", all).Split(','))
        {
            var entry = part.Trim();
            var close = entry.IndexOf('>');
            Require(entry.StartsWith('<') && close > 1);
            var suffix = entry[(close + 1)..].Trim();
            Require(suffix.StartsWith("; rel=\"", StringComparison.Ordinal) && suffix.EndsWith('"'));
            var relation = suffix[7..^1];
            Require(relation is "next" or "prev" or "first" or "last");
            var page = Page(entry[1..close], origin, repository);
            Require(relations.TryAdd(relation, page));
        }
        int? next = relations.TryGetValue("next", out var n) ? n : null;
        int? last = relations.TryGetValue("last", out var l) ? l : advertisedLast;
        Require(next is null || next == current + 1);
        Require(!relations.TryGetValue("first", out var first) || first == 1);
        Require(!relations.TryGetValue("prev", out var previous) || previous == current - 1);
        Require(last is null || last >= current && (advertisedLast is null || last == advertisedLast));
        Require(next is null ? last is null || last == current : last is null || last >= next);
        return (next, last);
    }

    private static int Page(string text, Uri origin, GitHubRepositoryIdentity repository)
    {
        Require(Uri.TryCreate(text, UriKind.Absolute, out var uri));
        Require(uri!.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && text == uri.AbsoluteUri);
        var canonical = "/repos/" + repository.Owner + "/" + repository.Name + "/pulls";
        var numeric = "/repositories/" + repository.Id.ToString(CultureInfo.InvariantCulture) + "/pulls";
        Require(uri.AbsolutePath == canonical || uri.AbsolutePath == numeric);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var pieces = pair.Split('=');
            Require(pieces.Length == 2 && query.TryAdd(pieces[0], pieces[1]));
        }
        Require(query.Count == 5 && query.GetValueOrDefault("state") == "all"
            && query.GetValueOrDefault("sort") == "created" && query.GetValueOrDefault("direction") == "asc"
            && query.GetValueOrDefault("per_page") == "100");
        var raw = query.GetValueOrDefault("page");
        Require(int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            && page is > 0 and <= MaximumPages && raw == page.ToString(CultureInfo.InvariantCulture));
        return page;
    }
}
