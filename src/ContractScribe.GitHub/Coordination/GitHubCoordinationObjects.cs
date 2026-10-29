using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.Coordination;

internal sealed class GitHubPreparedCoordination
{
    internal GitHubPreparedCoordination(
        GitHubCoordinationDocument document,
        GitHubPublicationEvent? publicationEvent,
        byte[] stateBytes,
        string blobOid,
        string leafTreeOid,
        string rootTreeOid,
        string parentOid,
        string message,
        string commitOid)
    {
        Document = document;
        Event = publicationEvent;
        StateBytes = ImmutableArray.CreateRange(stateBytes);
        BlobOid = blobOid;
        LeafTreeOid = leafTreeOid;
        RootTreeOid = rootTreeOid;
        ParentOid = parentOid;
        Message = message;
        CommitOid = commitOid;
    }

    internal GitHubCoordinationDocument Document { get; }
    internal GitHubPublicationEvent? Event { get; }
    internal GitHubCoordinationState State => Event?.State ?? throw new GitHubCoordinationException();
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

    internal const string CheckpointPath = "checkpoint.json";
    internal const string PlanPath = "plan.json";
    internal const string ArchivePath = "archive-predecessor.json";
    internal const string EventPath = "publication-event.json";

    internal static GitHubPreparedCoordination Prepare(GitHubCoordinationState state) => PreparePublication(state, null);

    internal static GitHubPreparedCoordination PreparePublication(GitHubCoordinationState state,
        GitHubCoordinationDocument? predecessor)
    {
        var publication = new GitHubPublicationEvent(state, predecessor?.Publication);
        var eventOid = ObjectOid("blob", publication.Encode());
        var document = new GitHubCoordinationDocument(GitHubCoordinationDocument.CurrentFormat,
            state.RepositoryId, state.TargetRef, state.TargetCommitOid, state.CampaignLineage,
            state.CoordinationPredecessorOid, predecessor?.Checkpoint, predecessor?.Plan, predecessor?.Archive,
            new(eventOid, GitHubCoordinationDocument.CurrentCommit), predecessor?.Claim);
        return Prepare(document, publication);
    }

    internal static GitHubPreparedCoordination Prepare(GitHubCoordinationDocument document,
        GitHubPublicationEvent? publication = null)
    {
        var stateBytes = document.Encode();
        var blobOid = ObjectOid("blob", stateBytes);
        var leafEntries = Entries(document, blobOid);
        var leafOid = TreeOid(leafEntries);
        var rootOid = TreeOid([new(RootPath, GitHubTreeMode.Directory, leafOid, null)]);
        var parent = document.PredecessorOid == GitHubCoordinationDocument.ZeroOid
            ? document.TargetCommitOid : document.PredecessorOid;
        var message = "ContractScribe campaign coordination v1\n";
        var commitText = "tree " + rootOid + "\n"
            + "parent " + parent + "\n"
            + "author " + ActorName + " <" + ActorEmail + "> "
            + ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000\n"
            + "committer " + ActorName + " <" + ActorEmail + "> "
            + ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000\n\n"
            + message;
        return new(document, publication, stateBytes, blobOid, leafOid, rootOid, parent, message,
            ObjectOid("commit", Encoding.UTF8.GetBytes(commitText)));
    }

    private static ImmutableArray<GitHubTreeEntry> Entries(GitHubCoordinationDocument document, string stateOid)
    {
        var entries = new List<GitHubTreeEntry> { new(StatePath, GitHubTreeMode.File, stateOid, null) };
        if (document.Checkpoint is { } checkpoint) entries.Add(new(CheckpointPath, GitHubTreeMode.File, checkpoint.Oid, null));
        if (document.Plan is { } plan) entries.Add(new(PlanPath, GitHubTreeMode.File, plan.Oid, null));
        if (document.Archive is { } archive) entries.Add(new(ArchivePath, GitHubTreeMode.File, archive.Oid, null));
        if (document.Publication is { } publication) entries.Add(new(EventPath, GitHubTreeMode.File, publication.Oid, null));
        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static string TreeOid(ImmutableArray<GitHubTreeEntry> entries)
    {
        using var stream = new MemoryStream();
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
            stream.Write(TreeEntry(entry.Mode == GitHubTreeMode.Directory ? "40000" : "100644", entry.Path, entry.Oid));
        return ObjectOid("tree", stream.ToArray());
    }

    internal static void Authenticate(
        GitHubPreparedCoordination expected,
        GitHubCommit commit,
        GitHubTree root,
        GitHubTree leaf,
        GitHubBlob blob)
    {
        AuthenticateLayout(expected, root, leaf, blob);
        Require(commit.Oid == expected.CommitOid
            && commit.TreeOid == expected.RootTreeOid
            && commit.Parents.Length == 1
            && commit.Parents[0] == expected.ParentOid
            && commit.HasOwnedMessage(expected.Message)
            && Actor(commit.Author)
            && Actor(commit.Committer));
    }

    internal static void AuthenticateLayout(GitHubPreparedCoordination expected, GitHubTree root, GitHubTree leaf, GitHubBlob blob)
    {
        Require(blob.Oid == expected.BlobOid
            && blob.Bytes.AsSpan().SequenceEqual(expected.StateBytes.AsSpan()));
        var expectedEntries = LeafEntries(expected);
        Require(leaf.Oid == expected.LeafTreeOid && leaf.Entries.Length == expectedEntries.Length);
        foreach (var entry in expectedEntries)
            Require(leaf.Entries.Any(actual => actual.Path == entry.Path && actual.Mode == entry.Mode
                && actual.Oid == entry.Oid && (actual.Size is null || actual.Size >= 0)));
        var rootEntry = Single(root.Entries);
        Require(root.Oid == expected.RootTreeOid
            && rootEntry.Path == RootPath
            && rootEntry.Mode == GitHubTreeMode.Directory
            && rootEntry.Oid == expected.LeafTreeOid
            && rootEntry.Size is null);
    }

    internal static GitHubCreateCommit CommitRequest(GitHubPreparedCoordination prepared) => new(
        prepared.CommitOid,
        prepared.RootTreeOid,
        prepared.ParentOid,
        prepared.Message,
        new(ActorName, ActorEmail, ActorDate),
        new(ActorName, ActorEmail, ActorDate));

    internal static ImmutableArray<GitHubTreeEntry> LeafEntries(GitHubPreparedCoordination prepared) =>
        Entries(prepared.Document, prepared.BlobOid);

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

    internal static string ObjectOid(string type, ReadOnlySpan<byte> bytes)
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
