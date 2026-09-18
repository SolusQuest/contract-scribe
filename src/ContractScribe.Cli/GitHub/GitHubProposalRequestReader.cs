using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;

namespace ContractScribe.Cli;

// Caller-owned invocation authority for one github-proposal execution: the
// campaign lineage, immutable snapshot binding, state location and GitHub
// runtime claims required by the M5 publication contract. Passing local
// admission never establishes remote truth; H1/R1-R6 authenticate the claims.
internal sealed record GitHubProposalRequest(
    [property: JsonPropertyName("githubProposalRequestVersion")] int Version,
    string CampaignLineage,
    string Snapshot,
    string State,
    [property: JsonPropertyName("github")] GitHubPublicationConfiguration GitHub);

internal sealed record GitHubProposalRequestSnapshot(
    string Path, byte[] Digest, GitHubProposalRequest Request)
{
    internal bool Revalidate()
    {
        try
        {
            return SHA256.HashData(GitHubProposalRequestReader.ReadBounded(Path)).AsSpan().SequenceEqual(Digest);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}

internal static class GitHubProposalRequestReader
{
    internal const int MaximumBytes = 262_144;
    private const int MaximumSnapshotScalars = 128;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    internal static GitHubProposalRequestSnapshot Read(string path, string currentDirectory)
    {
        path = System.IO.Path.GetFullPath(path, currentDirectory);
        var bytes = ReadBounded(path);
        if (bytes.Length is <= 0 or > MaximumBytes || bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new JsonException();
        _ = new UTF8Encoding(false, true).GetString(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        ValidateClosedJson(document.RootElement);
        if (!document.RootElement.TryGetProperty("githubProposalRequestVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != 1)
            throw new JsonException();
        if (!document.RootElement.TryGetProperty("github", out var github) || github.ValueKind != JsonValueKind.Object)
            throw new JsonException();
        if (!github.TryGetProperty("transition", out var transition)
            || transition.ValueKind != JsonValueKind.String
            || transition.GetString() is not ("initial" or "same-snapshot-append" or "successor-after-merge" or "successor-after-closed-unmerged"))
            throw new JsonException();
        if (github.TryGetProperty("terminalPredecessor", out var terminal) && terminal.ValueKind != JsonValueKind.Null
            && (terminal.ValueKind != JsonValueKind.Object
                || !terminal.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.String
                || disposition.GetString() is not ("merged" or "closed-unmerged"))) throw new JsonException();
        var request = document.RootElement.Deserialize<GitHubProposalRequest>(Options)
            ?? throw new JsonException();
        ValidateShape(request);
        var snapshot = new GitHubProposalRequestSnapshot(path, SHA256.HashData(bytes), request);
        if (!snapshot.Revalidate()) throw new JsonException();
        return snapshot;
    }

    internal static byte[] ReadBounded(string path)
    {
        if (!CliPreflight.IsRegularFileNoFollow(path)) throw new JsonException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw new JsonException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || !CliPreflight.IsRegularFileNoFollow(path)) throw new JsonException();
        return bytes;
    }

    private static void ValidateClosedJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(DecodeName(property))) throw new JsonException();
                ValidateClosedJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > GitHubPublicationContract.MaximumChangedFiles) throw new JsonException();
            foreach (var item in value.EnumerateArray()) ValidateClosedJson(item);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = DecodeString(value);
            if (text.Length is 0 || Encoding.UTF8.GetByteCount(text) > 4096 || text.Any(char.IsControl))
                throw new JsonException();
        }
    }

    private static string DecodeString(JsonElement value)
    {
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw new JsonException("Invalid escaped Unicode scalar."); }
    }

    private static string DecodeName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw new JsonException("Invalid escaped Unicode property name."); }
    }

    private static void ValidateShape(GitHubProposalRequest request)
    {
        if (request.Version != 1
            || !GitHubPublicationFactory.IsCampaignLineage(request.CampaignLineage)
            || !CampaignStateFactory.IsOpaqueId(request.Snapshot, MaximumSnapshotScalars))
            throw new JsonException();
        var configuration = request.GitHub;
        // Current-candidate facts are deliberately absent here; H1 supplies and verifies them.
        // Validate the independently decidable scalar and transition shape before campaign work.
        if (!GitHubPublicationFactory.IsOpaqueIdentifier(configuration.OperationId)
            || !GitHubPublicationFactory.IsOpaqueIdentifier(configuration.GenerationId)
            || !GitHubPublicationFactory.IsRefName(configuration.TargetRef)
            || !configuration.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            || !GitHubPublicationFactory.IsGitOid(configuration.ExpectedBaseCommitOid, allowMissing: false)
            || configuration.Policy is null
            || configuration.Policy.MaximumDocumentationBlocks <= 0
            || configuration.Policy.MaximumDistinctChangedFiles <= 0
            || configuration.Policy.MaximumCumulativePatchBytes <= 0
            || ((configuration.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
                != (configuration.AppendPredecessor is not null))
            || ((configuration.Transition is GitHubPublicationTransitionKind.SuccessorAfterMerge
                    or GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged)
                != (configuration.TerminalPredecessor is not null))
            || (configuration.ClosedUnmergedSuccessorAuthorization is not null
                && configuration.Transition != GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged))
            throw new JsonException();
        foreach (var part in new[] { configuration.RepositoryOwner, configuration.RepositoryName })
            if (part is null || part.Length is 0 or > 100
                || part is "." or ".." || !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new JsonException();
    }
}
