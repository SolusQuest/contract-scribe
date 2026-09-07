using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.GitData;

internal static class GitHubProposalObjects
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly GitHubCommitActor Actor = new("ContractScribe",
        "contract-scribe@users.noreply.github.com", DateTimeOffset.FromUnixTimeSeconds(946684800));

    internal static string ObjectOid(string type, ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes(type + " "
            + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static byte[] TreeBytes(ImmutableArray<GitHubTreeEntry> entries)
    {
        Require(!entries.IsDefault && entries.Length <= 100_000);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long requestBytes = 32;
        foreach (var entry in entries)
        {
            Require(entry.Path.Length is > 0 and <= 1024 && entry.Path is not ("." or "..")
                && !entry.Path.Any(c => c is '/' or '\\' or '\0') && names.Add(entry.Path));
            Require(entry.Oid.Length == 40 && entry.Oid.Any(c => c != '0')
                && entry.Oid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            requestBytes = checked(requestBytes + (long)entry.Path.Length * 6 + 128);
            Require(requestBytes <= 24 * 1024 * 1024);
        }
        using var stream = new MemoryStream();
        foreach (var entry in entries.OrderBy(e => e, GitOrder.Instance))
        {
            stream.Write(Utf8.GetBytes(Mode(entry.Mode) + " " + entry.Path));
            stream.WriteByte(0);
            stream.Write(Convert.FromHexString(entry.Oid));
        }
        return stream.ToArray();
    }

    internal static string TreeOid(ImmutableArray<GitHubTreeEntry> entries) => ObjectOid("tree", TreeBytes(entries));

    internal static GitHubCreateCommit Commit(string tree, string parent, string operation, string generationKey)
    {
        var message = "ContractScribe proposal v1\noperation=" + operation
            + "\ngeneration=" + generationKey + "\n";
        var request = new GitHubCreateCommit("", tree, parent, message, Actor, Actor);
        return request with { ExpectedOid = ObjectOid("commit", CommitBytes(request)) };
    }

    internal static byte[] CommitBytes(GitHubCreateCommit request) => Utf8.GetBytes(
        "tree " + request.TreeOid + "\nparent " + request.ParentOid + "\n"
        + ActorLine("author", request.Author) + ActorLine("committer", request.Committer)
        + "\n" + request.Message);

    private static string ActorLine(string kind, GitHubCommitActor actor) => kind + " " + actor.Name
        + " <" + actor.Email + "> " + actor.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        + " +0000\n";

    internal static bool ExactCommit(GitHubCommit actual, GitHubCreateCommit expected) =>
        actual.Oid == expected.ExpectedOid && actual.TreeOid == expected.TreeOid
        && actual.Parents.Length == 1 && actual.Parents[0] == expected.ParentOid
        && actual.Message == expected.Message && actual.Author == expected.Author
        && actual.Committer == expected.Committer
        && actual.Author.Date.Offset == TimeSpan.Zero && actual.Committer.Date.Offset == TimeSpan.Zero;

    internal static string Mode(GitHubTreeMode mode) => mode switch
    {
        GitHubTreeMode.File => "100644",
        GitHubTreeMode.Executable => "100755",
        GitHubTreeMode.Directory => "40000",
        GitHubTreeMode.SymbolicLink => "120000",
        GitHubTreeMode.Submodule => "160000",
        _ => throw new GitHubProposalException(),
    };

    internal static void Require(bool condition)
    {
        if (!condition) throw new GitHubProposalException();
    }

    private sealed class GitOrder : IComparer<GitHubTreeEntry>
    {
        internal static readonly GitOrder Instance = new();
        public int Compare(GitHubTreeEntry? x, GitHubTreeEntry? y)
        {
            var left = Utf8.GetBytes(x!.Path + (x.Mode == GitHubTreeMode.Directory ? "/" : ""));
            var right = Utf8.GetBytes(y!.Path + (y.Mode == GitHubTreeMode.Directory ? "/" : ""));
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}

internal sealed class GitHubProposalException : Exception
{
    internal GitHubProposalException() : base("The proposal Git object boundary rejected the operation.") { }
    public override string ToString() => Message;
}
