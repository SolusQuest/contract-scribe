using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed class GitHubPreparedCoordination
{
    internal GitHubPreparedCoordination(
        GitHubCoordinationState state,
        byte[] stateBytes,
        string blobOid,
        string leafTreeOid,
        string rootTreeOid,
        string parentOid,
        string message,
        string commitOid)
    {
        State = state;
        StateBytes = ImmutableArray.CreateRange(stateBytes);
        BlobOid = blobOid;
        LeafTreeOid = leafTreeOid;
        RootTreeOid = rootTreeOid;
        ParentOid = parentOid;
        Message = message;
        CommitOid = commitOid;
    }

    internal GitHubCoordinationState State { get; }
    internal ImmutableArray<byte> StateBytes { get; }
    internal string BlobOid { get; }
    internal string LeafTreeOid { get; }
    internal string RootTreeOid { get; }
    internal string ParentOid { get; }
    internal string Message { get; }
    internal string CommitOid { get; }
    public override string ToString() => nameof(GitHubPreparedCoordination);
}

internal static class GitHubCoordinationObjects
{
    internal const string RootPath = ".contract-scribe";
    internal const string StatePath = "coordination-state-v1.json";
    internal const string ActorName = "ContractScribe";
    internal const string ActorEmail = "contract-scribe@users.noreply.github.com";
    internal const long ActorUnixSeconds = 946684800;
    internal static readonly DateTimeOffset ActorDate = DateTimeOffset.FromUnixTimeSeconds(ActorUnixSeconds);

    internal static GitHubPreparedCoordination Prepare(GitHubCoordinationState state)
    {
        var stateBytes = GitHubCoordinationCodec.Encode(state);
        var blobOid = ObjectOid("blob", stateBytes);
        var leafBytes = TreeEntry("100644", StatePath, blobOid);
        var leafOid = ObjectOid("tree", leafBytes);
        var rootBytes = TreeEntry("40000", RootPath, leafOid);
        var rootOid = ObjectOid("tree", rootBytes);
        var parent = state.CoordinationPredecessorOid.All(character => character == '0')
            ? state.TargetCommitOid : state.CoordinationPredecessorOid;
        var message = "ContractScribe coordination v1\n"
            + "operation=" + state.OperationCommitmentSha256 + "\n"
            + "stage=" + GitHubCoordinationCodec.Stage(state.Stage) + "\n";
        var commitText = "tree " + rootOid + "\n"
            + "parent " + parent + "\n"
            + "author " + ActorName + " <" + ActorEmail + "> "
            + ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000\n"
            + "committer " + ActorName + " <" + ActorEmail + "> "
            + ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000\n\n"
            + message;
        var commitOid = ObjectOid("commit", Encoding.UTF8.GetBytes(commitText));
        return new(state, stateBytes, blobOid, leafOid, rootOid, parent, message, commitOid);
    }

    internal static void Authenticate(
        GitHubPreparedCoordination expected,
        GitHubCommit commit,
        GitHubTree root,
        GitHubTree leaf,
        GitHubBlob blob)
    {
        Require(blob.Oid == expected.BlobOid
            && blob.Bytes.AsSpan().SequenceEqual(expected.StateBytes.AsSpan()));
        var leafEntry = Single(leaf.Entries);
        Require(leaf.Oid == expected.LeafTreeOid
            && leafEntry.Path == StatePath
            && leafEntry.Mode == GitHubTreeMode.File
            && leafEntry.Oid == expected.BlobOid
            && (leafEntry.Size is null || leafEntry.Size == expected.StateBytes.Length));
        var rootEntry = Single(root.Entries);
        Require(root.Oid == expected.RootTreeOid
            && rootEntry.Path == RootPath
            && rootEntry.Mode == GitHubTreeMode.Directory
            && rootEntry.Oid == expected.LeafTreeOid
            && rootEntry.Size is null);
        Require(commit.Oid == expected.CommitOid
            && commit.TreeOid == expected.RootTreeOid
            && commit.Parents.Length == 1
            && commit.Parents[0] == expected.ParentOid
            && commit.Message == expected.Message
            && Actor(commit.Author)
            && Actor(commit.Committer));
    }

    internal static GitHubCreateCommit CommitRequest(GitHubPreparedCoordination prepared) => new(
        prepared.CommitOid,
        prepared.RootTreeOid,
        prepared.ParentOid,
        prepared.Message,
        new(ActorName, ActorEmail, ActorDate),
        new(ActorName, ActorEmail, ActorDate));

    internal static ImmutableArray<GitHubTreeEntry> LeafEntries(GitHubPreparedCoordination prepared) =>
        [new(StatePath, GitHubTreeMode.File, prepared.BlobOid, null)];

    internal static ImmutableArray<GitHubTreeEntry> RootEntries(GitHubPreparedCoordination prepared) =>
        [new(RootPath, GitHubTreeMode.Directory, prepared.LeafTreeOid, null)];

    private static bool Actor(GitHubCommitActor actor) => actor.Name == ActorName
        && actor.Email == ActorEmail
        && actor.Date.ToUnixTimeSeconds() == ActorUnixSeconds
        && actor.Date.Offset == TimeSpan.Zero;

    private static GitHubTreeEntry Single(ImmutableArray<GitHubTreeEntry> entries)
    {
        Require(entries.Length == 1);
        return entries[0];
    }

    private static string ObjectOid(string type, ReadOnlySpan<byte> bytes)
    {
        var header = Encoding.ASCII.GetBytes(type + " "
            + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] TreeEntry(string mode, string name, string oid)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes(mode + " " + name));
        stream.WriteByte(0);
        stream.Write(Convert.FromHexString(oid));
        return stream.ToArray();
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new GitHubCoordinationException();
    }
}
