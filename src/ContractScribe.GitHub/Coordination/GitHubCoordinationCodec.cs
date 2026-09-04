using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContractScribe.Core;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Coordination;

internal static class GitHubCoordinationCodec
{
    // JavaScriptEncoder.Default can expand each UTF-16 code unit to six bytes;
    // one Unicode scalar can occupy two UTF-16 code units. The fixed allowance
    // covers every non-path property, punctuation, hashes, refs and numbers.
    internal const int MaximumStateBytes = 64 * 1024
        + GitHubPublicationContract.MaximumChangedFiles
            * (GitHubPublicationContract.MaximumPathScalars * 12 + 160);
    private const int MaximumJsonDepth = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] PropertyNames =
    [
        "version", "stage", "repositoryId", "targetRef", "targetCommitOid",
        "snapshotCommitmentSha256", "authorityCommitmentSha256", "policyCommitmentSha256",
        "operationId", "operationCommitmentSha256", "currentCandidateCommitmentSha256",
        "precedingOperationId", "precedingAuthorityCommitmentSha256",
        "precedingCandidateCommitmentSha256", "generationId", "transition",
        "coordinationPredecessorOid", "contentCommitOid", "proposalRefOid",
        "proposalCommitOid", "proposalTreeOid", "pullRequestCreationOperationCommitmentSha256",
        "pullRequestNumber", "expectedBaseOid", "observedBaseOid", "ownershipMarkerSha256",
        "cumulativeDocumentationBlocks", "cumulativePatchBytes", "cumulativeChangedFiles",
    ];

    internal static GitHubCoordinationState CreateClaim(
        ValidatedGitHubPublicationAuthority authority,
        string coordinationPredecessorOid) => new(
        GitHubCoordinationStage.Claimed,
        authority.RepositoryOwner + "/" + authority.RepositoryName,
        authority.TargetRef,
        authority.ExpectedBaseCommitOid,
        authority.SnapshotCommitmentSha256,
        authority.AuthorityCommitmentSha256,
        authority.PolicyCommitmentSha256,
        authority.OperationId,
        authority.OperationCommitmentSha256,
        authority.CandidateCommitmentSha256,
        authority.PrecedingOperationId,
        authority.PrecedingAuthorityCommitmentSha256,
        authority.PrecedingCandidateCommitmentSha256,
        authority.GenerationId,
        Transition(authority.Transition),
        coordinationPredecessorOid,
        null, null, null, null, null, null, null, null, null,
        authority.CumulativeDocumentationBlocks,
        authority.CumulativePatchBytes,
        authority.ChangedFiles.Select(file =>
            new GitHubCoordinationChangedFile(file.Path, file.CandidateFileSha256)).ToImmutableArray());

    internal static GitHubCoordinationState WithStage(
        GitHubCoordinationState current,
        GitHubCoordinationStage stage,
        string predecessorOid,
        string? contentCommitOid = null,
        string? proposalRefOid = null,
        string? proposalCommitOid = null,
        string? proposalTreeOid = null,
        string? pullRequestCreationOperationCommitmentSha256 = null,
        int? pullRequestNumber = null,
        string? expectedBaseOid = null,
        string? observedBaseOid = null,
        string? ownershipMarkerSha256 = null) => new(
        stage, current.RepositoryId, current.TargetRef, current.TargetCommitOid,
        current.SnapshotCommitmentSha256, current.AuthorityCommitmentSha256,
        current.PolicyCommitmentSha256, current.OperationId,
        current.OperationCommitmentSha256, current.CurrentCandidateCommitmentSha256,
        current.PrecedingOperationId, current.PrecedingAuthorityCommitmentSha256,
        current.PrecedingCandidateCommitmentSha256, current.GenerationId,
        current.Transition, predecessorOid, contentCommitOid, proposalRefOid,
        proposalCommitOid, proposalTreeOid, pullRequestCreationOperationCommitmentSha256,
        pullRequestNumber, expectedBaseOid, observedBaseOid, ownershipMarkerSha256,
        current.CumulativeDocumentationBlocks, current.CumulativePatchBytes,
        current.CumulativeChangedFiles);

    internal static byte[] Encode(GitHubCoordinationState state)
    {
        Validate(state);
        var estimate = EstimateBytes(state);
        Require(estimate <= MaximumStateBytes);
        using var stream = new MemoryStream((int)estimate);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", GitHubPublicationContract.Version);
            writer.WriteString("stage", Stage(state.Stage));
            writer.WriteString("repositoryId", state.RepositoryId);
            writer.WriteString("targetRef", state.TargetRef);
            writer.WriteString("targetCommitOid", state.TargetCommitOid);
            writer.WriteString("snapshotCommitmentSha256", state.SnapshotCommitmentSha256);
            writer.WriteString("authorityCommitmentSha256", state.AuthorityCommitmentSha256);
            writer.WriteString("policyCommitmentSha256", state.PolicyCommitmentSha256);
            writer.WriteString("operationId", state.OperationId);
            writer.WriteString("operationCommitmentSha256", state.OperationCommitmentSha256);
            writer.WriteString("currentCandidateCommitmentSha256", state.CurrentCandidateCommitmentSha256);
            WriteOptional(writer, "precedingOperationId", state.PrecedingOperationId);
            WriteOptional(writer, "precedingAuthorityCommitmentSha256", state.PrecedingAuthorityCommitmentSha256);
            WriteOptional(writer, "precedingCandidateCommitmentSha256", state.PrecedingCandidateCommitmentSha256);
            writer.WriteString("generationId", state.GenerationId);
            writer.WriteString("transition", state.Transition);
            writer.WriteString("coordinationPredecessorOid", state.CoordinationPredecessorOid);
            WriteOptional(writer, "contentCommitOid", state.ContentCommitOid);
            WriteOptional(writer, "proposalRefOid", state.ProposalRefOid);
            WriteOptional(writer, "proposalCommitOid", state.ProposalCommitOid);
            WriteOptional(writer, "proposalTreeOid", state.ProposalTreeOid);
            WriteOptional(writer, "pullRequestCreationOperationCommitmentSha256", state.PullRequestCreationOperationCommitmentSha256);
            if (state.PullRequestNumber is { } number) writer.WriteNumber("pullRequestNumber", number);
            else writer.WriteNull("pullRequestNumber");
            WriteOptional(writer, "expectedBaseOid", state.ExpectedBaseOid);
            WriteOptional(writer, "observedBaseOid", state.ObservedBaseOid);
            WriteOptional(writer, "ownershipMarkerSha256", state.OwnershipMarkerSha256);
            writer.WriteNumber("cumulativeDocumentationBlocks", state.CumulativeDocumentationBlocks);
            writer.WriteNumber("cumulativePatchBytes", state.CumulativePatchBytes);
            writer.WriteStartArray("cumulativeChangedFiles");
            foreach (var file in state.CumulativeChangedFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("candidateSha256", file.CandidateSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        var bytes = stream.ToArray();
        Require(bytes.Length <= MaximumStateBytes);
        return bytes;
    }

    internal static GitHubCoordinationState Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return DecodeCore(bytes);
        }
        catch (GitHubCoordinationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or OverflowException or DecoderFallbackException)
        {
            throw Fail();
        }
    }

    private static GitHubCoordinationState DecodeCore(ReadOnlyMemory<byte> bytes)
    {
        Require(bytes.Length is > 0 and <= MaximumStateBytes
            && bytes.Span[^1] == (byte)'\n'
            && bytes.Span[0] == (byte)'{');
        _ = StrictUtf8.GetString(bytes.Span);
        using var document = JsonDocument.Parse(bytes[..^1], new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object);
        var properties = root.EnumerateObject().ToArray();
        Require(properties.Length == PropertyNames.Length);
        for (var index = 0; index < PropertyNames.Length; index++)
            Require(properties[index].NameEquals(PropertyNames[index]));
        Require(root.GetProperty("version").GetInt32() == GitHubPublicationContract.Version);
        var filesElement = root.GetProperty("cumulativeChangedFiles");
        Require(filesElement.ValueKind == JsonValueKind.Array
            && filesElement.GetArrayLength() <= GitHubPublicationContract.MaximumChangedFiles);
        var files = ImmutableArray.CreateBuilder<GitHubCoordinationChangedFile>(filesElement.GetArrayLength());
        foreach (var item in filesElement.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object);
            var itemProperties = item.EnumerateObject().ToArray();
            Require(itemProperties.Length == 2
                && itemProperties[0].NameEquals("path")
                && itemProperties[1].NameEquals("candidateSha256"));
            files.Add(new(RequiredString(item, "path"), RequiredString(item, "candidateSha256")));
        }
        var state = new GitHubCoordinationState(
            ParseStage(RequiredString(root, "stage")),
            RequiredString(root, "repositoryId"),
            RequiredString(root, "targetRef"),
            RequiredString(root, "targetCommitOid"),
            RequiredString(root, "snapshotCommitmentSha256"),
            RequiredString(root, "authorityCommitmentSha256"),
            RequiredString(root, "policyCommitmentSha256"),
            RequiredString(root, "operationId"),
            RequiredString(root, "operationCommitmentSha256"),
            RequiredString(root, "currentCandidateCommitmentSha256"),
            OptionalString(root, "precedingOperationId"),
            OptionalString(root, "precedingAuthorityCommitmentSha256"),
            OptionalString(root, "precedingCandidateCommitmentSha256"),
            RequiredString(root, "generationId"),
            RequiredString(root, "transition"),
            RequiredString(root, "coordinationPredecessorOid"),
            OptionalString(root, "contentCommitOid"),
            OptionalString(root, "proposalRefOid"),
            OptionalString(root, "proposalCommitOid"),
            OptionalString(root, "proposalTreeOid"),
            OptionalString(root, "pullRequestCreationOperationCommitmentSha256"),
            OptionalInt(root, "pullRequestNumber"),
            OptionalString(root, "expectedBaseOid"),
            OptionalString(root, "observedBaseOid"),
            OptionalString(root, "ownershipMarkerSha256"),
            root.GetProperty("cumulativeDocumentationBlocks").GetInt32(),
            root.GetProperty("cumulativePatchBytes").GetInt64(),
            files.MoveToImmutable());
        Validate(state);
        Require(bytes.Span.SequenceEqual(Encode(state)));
        return state;
    }

    internal static void Validate(GitHubCoordinationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(RepositoryId(state.RepositoryId)
            && RefName(state.TargetRef) && IsOid(state.TargetCommitOid)
            && Hex(state.SnapshotCommitmentSha256, 64)
            && Hex(state.AuthorityCommitmentSha256, 64)
            && Hex(state.PolicyCommitmentSha256, 64)
            && Opaque(state.OperationId)
            && Hex(state.OperationCommitmentSha256, 64)
            && Hex(state.CurrentCandidateCommitmentSha256, 64)
            && Opaque(state.GenerationId)
            && Transition(state.Transition)
            && IsOid(state.CoordinationPredecessorOid, zero: true)
            && state.CumulativeDocumentationBlocks is > 0
                and <= CampaignStateContract.MaximumActivePatchBlocks
            && state.CumulativePatchBytes is > 0
                and <= CampaignStateContract.MaximumPatchBytes
            && !state.CumulativeChangedFiles.IsDefaultOrEmpty
            && state.CumulativeChangedFiles.Length <= GitHubPublicationContract.MaximumChangedFiles
            && state.CumulativeChangedFiles.Length <= state.CumulativeDocumentationBlocks
            && state.CumulativeChangedFiles.Length <= state.CumulativePatchBytes);
        var append = state.Transition == "same-snapshot-append";
        Require(append == (state.PrecedingOperationId is not null)
            && append == (state.PrecedingAuthorityCommitmentSha256 is not null)
            && append == (state.PrecedingCandidateCommitmentSha256 is not null));
        if (append)
            Require(Opaque(state.PrecedingOperationId!)
                && Hex(state.PrecedingAuthorityCommitmentSha256, 64)
                && Hex(state.PrecedingCandidateCommitmentSha256, 64));
        var previous = string.Empty;
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in state.CumulativeChangedFiles)
        {
            if (file is null) throw Fail();
            Require(RepositoryPath(file.Path)
                && Hex(file.CandidateSha256, 64)
                && string.CompareOrdinal(previous, file.Path) < 0
                && folded.Add(file.Path));
            previous = file.Path;
        }
        ValidateStage(state);
    }

    internal static string Stage(GitHubCoordinationStage stage) => stage switch
    {
        GitHubCoordinationStage.Claimed => "claimed",
        GitHubCoordinationStage.ContentCreated => "content-created",
        GitHubCoordinationStage.ProposalRefAdvanced => "proposal-ref-advanced",
        GitHubCoordinationStage.PullRequestCreated => "pr-created",
        GitHubCoordinationStage.Published => "published",
        GitHubCoordinationStage.AwaitingReview => "awaiting-review",
        GitHubCoordinationStage.StaleDraft => "stale-draft",
        GitHubCoordinationStage.Stale => "stale",
        GitHubCoordinationStage.Merged => "merged",
        GitHubCoordinationStage.ClosedUnmerged => "closed-unmerged",
        _ => throw Fail(),
    };

    private static GitHubCoordinationStage ParseStage(string value) => value switch
    {
        "claimed" => GitHubCoordinationStage.Claimed,
        "content-created" => GitHubCoordinationStage.ContentCreated,
        "proposal-ref-advanced" => GitHubCoordinationStage.ProposalRefAdvanced,
        "pr-created" => GitHubCoordinationStage.PullRequestCreated,
        "published" => GitHubCoordinationStage.Published,
        "awaiting-review" => GitHubCoordinationStage.AwaitingReview,
        "stale-draft" => GitHubCoordinationStage.StaleDraft,
        "stale" => GitHubCoordinationStage.Stale,
        "merged" => GitHubCoordinationStage.Merged,
        "closed-unmerged" => GitHubCoordinationStage.ClosedUnmerged,
        _ => throw Fail(),
    };

    private static void ValidateStage(GitHubCoordinationState state)
    {
        var content = IsOidOrNull(state.ContentCommitOid);
        var proposalValues = IsOidOrNull(state.ProposalRefOid) && IsOidOrNull(state.ProposalCommitOid)
            && IsOidOrNull(state.ProposalTreeOid);
        var anyProposal = state.ProposalRefOid is not null || state.ProposalCommitOid is not null || state.ProposalTreeOid is not null;
        var completeProposal = state.ProposalRefOid is not null
            && state.ProposalCommitOid is not null && state.ProposalTreeOid is not null;
        Require(content && proposalValues && (!anyProposal || completeProposal
            && state.ContentCommitOid is not null
            && state.ProposalRefOid == state.ContentCommitOid
            && state.ProposalCommitOid == state.ContentCommitOid));
        var pr = state.PullRequestCreationOperationCommitmentSha256 is not null
            || state.PullRequestNumber is not null || state.OwnershipMarkerSha256 is not null;
        var bases = state.ExpectedBaseOid is not null || state.ObservedBaseOid is not null;
        if (pr)
        {
            Require(completeProposal && Hex(state.PullRequestCreationOperationCommitmentSha256, 64)
                && state.PullRequestNumber > 0 && Hex(state.OwnershipMarkerSha256, 64)
                && IsOid(state.ExpectedBaseOid) && IsOid(state.ObservedBaseOid));
        }
        else Require(state.PullRequestCreationOperationCommitmentSha256 is null
            && state.PullRequestNumber is null && state.OwnershipMarkerSha256 is null);
        if (bases) Require(IsOid(state.ExpectedBaseOid) && IsOid(state.ObservedBaseOid)
            && state.ExpectedBaseOid == state.TargetCommitOid);
        else Require(state.ExpectedBaseOid is null && state.ObservedBaseOid is null);

        switch (state.Stage)
        {
            case GitHubCoordinationStage.Claimed:
                Require(state.ContentCommitOid is null && !anyProposal && !pr && !bases);
                break;
            case GitHubCoordinationStage.ContentCreated:
                Require(state.ContentCommitOid is not null && !anyProposal && !pr && !bases);
                break;
            case GitHubCoordinationStage.ProposalRefAdvanced:
                Require(completeProposal && !pr && !bases);
                break;
            case GitHubCoordinationStage.PullRequestCreated:
            case GitHubCoordinationStage.Published:
            case GitHubCoordinationStage.AwaitingReview:
                Require(pr && state.ExpectedBaseOid == state.ObservedBaseOid);
                break;
            case GitHubCoordinationStage.StaleDraft:
                Require(pr && state.ExpectedBaseOid != state.ObservedBaseOid);
                break;
            case GitHubCoordinationStage.Stale:
                Require(!pr && bases && state.ExpectedBaseOid != state.ObservedBaseOid
                    && (!anyProposal || completeProposal));
                break;
            case GitHubCoordinationStage.Merged:
            case GitHubCoordinationStage.ClosedUnmerged:
                Require(pr);
                break;
            default:
                throw Fail();
        }
    }

    internal static void ValidatePullRequestOwnership(
        GitHubCoordinationState state,
        string proposalRef)
    {
        if (state.PullRequestCreationOperationCommitmentSha256 is null) return;
        Require(RefName(proposalRef));
        var commitment = PullRequestCreationCommitment(state, proposalRef);
        Require(commitment == state.PullRequestCreationOperationCommitmentSha256
            && MarkerHash(commitment) == state.OwnershipMarkerSha256);
    }

    private static string PullRequestCreationCommitment(
        GitHubCoordinationState state,
        string proposalRef)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "domain", "contract-scribe/github-pull-request-creation/v1");
        Add(hash, "version", GitHubPublicationContract.Version);
        Add(hash, "repository-id", state.RepositoryId);
        Add(hash, "target-ref", state.TargetRef);
        Add(hash, "target-commit-oid", state.TargetCommitOid);
        Add(hash, "authority-commitment", state.AuthorityCommitmentSha256);
        Add(hash, "operation-commitment", state.OperationCommitmentSha256);
        Add(hash, "generation-id", state.GenerationId);
        Add(hash, "proposal-ref", proposalRef);
        Add(hash, "proposal-commit-oid", state.ProposalCommitOid!);
        Add(hash, "proposal-tree-oid", state.ProposalTreeOid!);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string MarkerHash(string commitment) => Convert.ToHexStringLower(
        SHA256.HashData(StrictUtf8.GetBytes(
            "<!-- contract-scribe-publication-v1 ownership=sha256:" + commitment + " -->\n")));

    private static void Add(IncrementalHash hash, string label, string value)
    {
        Append(hash, StrictUtf8.GetBytes(label));
        Append(hash, StrictUtf8.GetBytes(value));
    }

    private static void Add(IncrementalHash hash, string label, int value)
    {
        Append(hash, StrictUtf8.GetBytes(label));
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static long EstimateBytes(GitHubCoordinationState state)
    {
        long estimate = 4096;
        foreach (var value in new[] { state.RepositoryId, state.TargetRef, state.OperationId,
            state.PrecedingOperationId, state.GenerationId })
            if (value is not null) estimate = checked(estimate + (long)value.Length * 6);
        foreach (var file in state.CumulativeChangedFiles)
            estimate = checked(estimate + (long)file.Path.Length * 6 + 160);
        return estimate;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString();
    }

    private static int? OptionalInt(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.Number);
        return value.GetInt32();
    }

    private static bool RepositoryId(string value)
    {
        var slash = value.IndexOf('/');
        return slash > 0 && slash == value.LastIndexOf('/')
            && RepositoryPart(value[..slash]) && RepositoryPart(value[(slash + 1)..]);
    }

    private static bool Opaque(string? value) => value is { Length: > 0 }
        && value.EnumerateRunes().Count() <= GitHubPublicationContract.MaximumIdentifierScalars
        && char.IsAsciiLetterOrDigit(value[0])
        && value[1..].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool RepositoryPath(string value) => value.Length > 0
        && value.EnumerateRunes().Count() <= GitHubPublicationContract.MaximumPathScalars
        && !value.StartsWith('/') && !value.StartsWith('\\')
        && !(value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        && !value.Contains('\\') && !value.Contains("//", StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment is "" or "." or "..")
        && !value.Any(character => character is '\0' or '\r' or '\n')
        && ValidUtf16(value);

    private static bool Transition(string value) => value is "initial" or "same-snapshot-append"
        or "successor-after-merge" or "successor-after-closed-unmerged";

    private static string Transition(GitHubPublicationTransitionKind value) => value switch
    {
        GitHubPublicationTransitionKind.Initial => "initial",
        GitHubPublicationTransitionKind.SameSnapshotAppend => "same-snapshot-append",
        GitHubPublicationTransitionKind.SuccessorAfterMerge => "successor-after-merge",
        GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged => "successor-after-closed-unmerged",
        _ => throw Fail(),
    };

    private static bool IsOidOrNull(string? value) => value is null || IsOid(value);
    private static void Require(bool condition)
    {
        if (!condition) throw Fail();
    }
    private static GitHubCoordinationException Fail() => new();
}

internal sealed class GitHubCoordinationException : Exception
{
    internal GitHubCoordinationException() : base("The GitHub coordination boundary rejected the operation.") { }
    public override string ToString() => Message;
}
