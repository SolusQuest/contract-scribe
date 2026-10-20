using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Coordination;

internal sealed record GitHubCheckpointPointer(string Oid, string Sha256, long Revision);
internal sealed record GitHubPlanPointer(string Oid, string Sha256, string ExecutionCommitment, string BatchIdentity);
internal sealed record GitHubArchivePointer(string Oid, string WitnessSha256, string Anchor);
internal sealed record GitHubEventPointer(string Oid, string Anchor);
internal sealed record GitHubInvocationClaim(string Owner, long Epoch, long ExpiresUnixSeconds);

// All referenced blobs are real tree entries. "current" resolves from the containing
// commit after hashing; no payload contains its own future commit OID.
internal sealed record GitHubCoordinationDocument(
    string Format, string RepositoryId, string TargetRef, string TargetCommitOid, string CampaignLineage,
    string PredecessorOid, GitHubCheckpointPointer? Checkpoint, GitHubPlanPointer? Plan,
    GitHubArchivePointer? Archive, GitHubEventPointer? Publication, GitHubInvocationClaim? Claim)
{
    internal const string CurrentFormat = "campaign-coordination-v1";
    internal const string CurrentCommit = "current";
    internal const string ZeroOid = "0000000000000000000000000000000000000000";
    internal const int MaximumBytes = 32768;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    internal byte[] Encode()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, Options);
    }

    internal static GitHubCoordinationDocument Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            Check(bytes.Length <= MaximumBytes);
            var value = JsonSerializer.Deserialize<GitHubCoordinationDocument>(bytes.Span, Options);
            Check(value is not null);
            Check(bytes.Span.SequenceEqual(value!.Encode()));
            return value;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { throw new GitHubCoordinationException(); }
    }

    internal GitHubCoordinationDocument RetainAt(string head) => this with
    {
        Publication = Publication is { Anchor: CurrentCommit } publication ? publication with { Anchor = head } : Publication,
        Archive = Archive is { Anchor: CurrentCommit } archive ? archive with { Anchor = head } : Archive,
    };

    private void Validate()
    {
        Check(Format == CurrentFormat && !string.IsNullOrEmpty(RepositoryId) && RepositoryId.Length <= 512
            && !string.IsNullOrEmpty(CampaignLineage) && CampaignLineage.Length <= 512
            && TargetRef is { Length: > 0 and <= 1024 } && TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            && IsOid(TargetCommitOid) && IsOid(PredecessorOid, zero: true));
        if (Checkpoint is { } checkpoint)
            Check(IsOid(checkpoint.Oid) && Hex(checkpoint.Sha256, 64) && checkpoint.Revision is >= 0 and <= CampaignStateContract.MaximumObservation
                && Plan is not null);
        if (Plan is { } plan) Check(IsOid(plan.Oid) && Hex(plan.Sha256, 64)
            && Hex(plan.ExecutionCommitment, 64) && Hex(plan.BatchIdentity, 64));
        if (Archive is { } archive) Check(Checkpoint is not null && IsOid(archive.Oid)
            && Hex(archive.WitnessSha256, 64) && Anchor(archive.Anchor));
        if (Publication is { } publication) Check(IsOid(publication.Oid) && Anchor(publication.Anchor));
        if (Claim is { } claim) Check(Hex(claim.Owner, 64) && claim.Epoch > 0
            && claim.Epoch <= CampaignStateContract.MaximumObservation && claim.ExpiresUnixSeconds > 0);
    }

    private static bool Anchor(string value) => value == CurrentCommit || IsOid(value);
    private static void Check(bool condition) { if (!condition) throw new GitHubCoordinationException(); }
}

internal sealed record GitHubPublicationEvent(GitHubCoordinationState State, GitHubEventPointer? Previous)
{
    private sealed record Wire(string Format, JsonElement State, GitHubEventPointer? Previous);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new Wire("publication-event-v1",
        JsonSerializer.Deserialize<JsonElement>(GitHubCoordinationCodec.Encode(State)), Previous), Options);

    internal static GitHubPublicationEvent Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length > GitHubCoordinationCodec.MaximumStateBytes + 1024) throw new GitHubCoordinationException();
            var wire = JsonSerializer.Deserialize<Wire>(bytes.Span, Options);
            if (wire is null || wire.Format != "publication-event-v1"
                || wire.Previous is { } previous && (!IsOid(previous.Oid) || !IsOid(previous.Anchor)))
                throw new GitHubCoordinationException();
            var state = GitHubCoordinationCodec.Decode(System.Text.Encoding.UTF8.GetBytes(wire.State.GetRawText() + "\n"));
            var result = new GitHubPublicationEvent(state, wire.Previous);
            if (!bytes.Span.SequenceEqual(result.Encode())) throw new GitHubCoordinationException();
            return result;
        }
        catch (JsonException) { throw new GitHubCoordinationException(); }
    }
}
