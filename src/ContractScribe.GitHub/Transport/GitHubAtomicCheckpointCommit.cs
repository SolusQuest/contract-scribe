using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

// One finite scalar coordination write, followed by independent physical
// signed-object and ref verification. Publication rights remain owner-issued.
internal sealed class GitHubAtomicCheckpointCommit
{
    internal GitHubAtomicCheckpointCommit(GitHubCheckpointWrite write, GitHubRepositoryIdentity repository, GitHubRef predecessor,
        GitHubActor expectedPrincipal)
    {
        if (expectedPrincipal is null || expectedPrincipal.Id <= 0 || expectedPrincipal.Kind is not (GitHubActorKind.User or GitHubActorKind.Bot)
            || write.Prepared.Document.PredecessorOid == GitHubCoordinationDocument.ZeroOid
            || predecessor.Oid != write.Prepared.Document.PredecessorOid || predecessor.NodeId is not { Length: > 0 and <= 256 }
            || predecessor.Name != GitHubPublicationFactory.CreateCoordinationRef(
                GitHubPublicationFactory.CreateCampaignReadAuthority(repository.Owner, repository.Name,
                    write.Prepared.Document.TargetRef, write.Prepared.Document.CampaignLineage)))
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        Write = write;
        ExpectedPrincipal = expectedPrincipal;
        var operation = OperationCommitment(repository, predecessor.Name, predecessor.NodeId, write.Prepared);
        var nonce = Guid.NewGuid().ToString("N");
        Context = new(repository, operation, predecessor.Name, predecessor.NodeId, write.Prepared.Document.PredecessorOid,
            write.Prepared.RootTreeOid, "ContractScribe checkpoint v1\n\noperation=" + operation + "\nnonce=" + nonce + "\n", nonce);
        Files = write.Blobs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var entry = GitHubCoordinationObjects.LeafEntries(write.Prepared).Single(item => item.Oid == pair.Key);
            return new KeyValuePair<string, ImmutableArray<byte>>(GitHubCoordinationObjects.RootPath + "/" + entry.Path, pair.Value);
        }).ToImmutableArray();
    }

    internal GitHubCheckpointWrite Write { get; }
    internal GitHubActor ExpectedPrincipal { get; }
    internal GitHubCheckpointCommitContext Context { get; }
    internal ImmutableArray<KeyValuePair<string, ImmutableArray<byte>>> Files { get; }
    private int consumed;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;

    internal static bool Authenticates(GitHubPreparedCoordination prepared, GitHubCommit commit,
        GitHubRepositoryIdentity repository, string reference, string? refNodeId, GitHubActor expectedPrincipal)
    {
        if (prepared.Document.PredecessorOid == GitHubCoordinationDocument.ZeroOid
            || commit.CheckpointSignature is not { } signature || signature.Repository != repository
            || signature.RefNodeId != refNodeId || signature.Author.NodeId != expectedPrincipal.NodeId
            || signature.Author.Login != expectedPrincipal.Login
            || signature.Author.DatabaseId is { } id && id != expectedPrincipal.Id
            || commit.TreeOid != prepared.RootTreeOid || commit.Parents.Length != 1 || commit.Parents[0] != prepared.ParentOid)
            return false;
        var prefix = "ContractScribe checkpoint v1\n\noperation="
            + OperationCommitment(repository, reference, signature.RefNodeId, prepared) + "\nnonce=";
        var message = commit.Message.EndsWith('\n') ? commit.Message[..^1] : commit.Message;
        return message.Length == prefix.Length + 32 && message.StartsWith(prefix, StringComparison.Ordinal)
            && message.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static string OperationCommitment(GitHubRepositoryIdentity repository, string reference,
        string refNodeId, GitHubPreparedCoordination prepared) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            repository.Id.ToString(CultureInfo.InvariantCulture) + "\n" + repository.NodeId + "\n"
            + reference + "\n" + refNodeId + "\n" + prepared.CommitOid)));

    // An immutable effect observation is not a lease, Core acceptance or
    // permission to send. The owner retains the live authority gates.
    internal bool MatchesObservedCommit(GitHubCommit commit) =>
        commit.TreeOid == Context.TreeOid && commit.Parents.Length == 1 && commit.Parents[0] == Context.BeforeOid
        && commit.HasOwnedMessage(Context.Message);

    internal async ValueTask<GitHubCommit?> ObserveAggregatedAsync(GitHubApiClient client, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var read = await client.ReadCheckpointBundleAsync(this, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read.Value is not { } bundle || !MatchesObservedCommit(bundle.Commit)
            || bundle.Target.Oid != Write.Prepared.Document.TargetCommitOid || !MatchesPayloads(bundle.Blobs)) return null;
        // One HTTP response is not a snapshot. The current ref is fenced again
        // after all independent bytes and pointers have been authenticated.
        var final = await client.GetRefAsync(Context.Ref, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return final.Value == bundle.Reference ? bundle.Commit : null;
    }

    private bool MatchesPayloads(IReadOnlyDictionary<string, GitHubBlob> blobs)
    {
        var document = Write.Prepared.Document;
        if (document.Plan is { } plan && (!blobs.TryGetValue(GitHubCoordinationObjects.PlanPath, out var planBlob)
            || Convert.ToHexStringLower(SHA256.HashData(planBlob.Bytes.AsSpan())) != plan.Sha256)) return false;
        if (document.Archive is { } archive && (!blobs.TryGetValue(GitHubCoordinationObjects.ArchivePath, out var witness)
            || Convert.ToHexStringLower(SHA256.HashData(witness.Bytes.AsSpan())) != archive.WitnessSha256)) return false;
        if (document.Checkpoint is not { } checkpoint) return true;
        if (!blobs.TryGetValue(GitHubCoordinationObjects.CheckpointPath, out var checkpointBlob)) return false;
        var parsed = CampaignStateJson.Parse(checkpointBlob.Bytes.AsMemory());
        return parsed.IsValid && parsed.Artifact is { } artifact && artifact.Sha256 == checkpoint.Sha256
            && artifact.CheckpointRevision == checkpoint.Revision && artifact.State.Batch.Identity == document.Plan?.BatchIdentity
            && artifact.State.Snapshot.ExecutionCommitmentSha256 == document.Plan?.ExecutionCommitment;
    }

    // This reader is used only by an independent physical query, never by the mutation
    // parser. Rebuild the Git object from its observed metadata/signature and
    // require the actual requested OID, including GitHub's signed-commit bytes.
    internal static GitHubCommit ReadObservedCommit(JsonElement value, string oid)
    {
        var commit = Commit(value, oid);
        var verification = Property(value, "verification");
        var signed = Boolean(verification, "verified");
        var reason = String(verification, "reason", 64);
        string raw;
        if (!signed)
        {
            Require(reason == "unsigned" && Property(verification, "signature").ValueKind == JsonValueKind.Null
                && Property(verification, "payload").ValueKind == JsonValueKind.Null);
            Require(commit.Author.Date.Offset == TimeSpan.Zero && commit.Committer.Date.Offset == TimeSpan.Zero);
            raw = "tree " + commit.TreeOid + "\n" + string.Concat(commit.Parents.Select(parent => "parent " + parent + "\n"))
                + ActorLine("author", commit.Author) + ActorLine("committer", commit.Committer) + "\n" + commit.Message;
        }
        else
        {
            Require(reason == "valid");
            var payload = String(verification, "payload", 131072);
            var signature = String(verification, "signature", 16384);
            Require(!payload.Contains('\r') && !signature.Contains('\r') && signature.StartsWith("-----BEGIN PGP SIGNATURE-----\n", StringComparison.Ordinal)
                && signature.TrimEnd('\n').EndsWith("-----END PGP SIGNATURE-----", StringComparison.Ordinal));
            var separator = payload.IndexOf("\n\n", StringComparison.Ordinal);
            Require(separator > 0);
            var headers = payload[..separator].Split('\n');
            Require(headers.Length == commit.Parents.Length + 3 && headers[0] == "tree " + commit.TreeOid);
            for (var index = 0; index < commit.Parents.Length; index++) Require(headers[index + 1] == "parent " + commit.Parents[index]);
            Require(ObservedActor(headers[^2], "author", commit.Author) && ObservedActor(headers[^1], "committer", commit.Committer));
            Require(commit.HasOwnedMessage(payload[(separator + 2)..]));
            raw = payload[..(separator + 1)] + "gpgsig " + signature.TrimEnd('\n').Replace("\n", "\n ", StringComparison.Ordinal)
                + "\n" + payload[(separator + 1)..];
        }
        var hash = GitHubCoordinationObjects.ObjectOid("commit", Encoding.UTF8.GetBytes(raw));
        // REST can omit one final LF from the commit message. Only the exact
        // independently hashed raw object can select that projection.
        if (!signed && hash != oid && !raw.EndsWith('\n'))
            hash = GitHubCoordinationObjects.ObjectOid("commit", Encoding.UTF8.GetBytes(raw + "\n"));
        Require(hash == oid);
        return commit;
    }

    private static string ActorLine(string kind, GitHubCommitActor actor) => kind + " " + actor.Name + " <" + actor.Email + "> "
        + actor.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000\n";

    private static bool ObservedActor(string line, string kind, GitHubCommitActor actor)
    {
        var prefix = kind + " " + actor.Name + " <" + actor.Email + "> ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = line[prefix.Length..].Split(' ');
        return fields.Length == 2 && long.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
            && seconds == actor.Date.ToUnixTimeSeconds() && fields[1].Length == 5 && fields[1][0] is '+' or '-'
            && fields[1].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0
            && int.Parse(fields[1].AsSpan(1, 2), CultureInfo.InvariantCulture) <= 14
            && int.Parse(fields[1].AsSpan(3, 2), CultureInfo.InvariantCulture) < 60;
    }
}
