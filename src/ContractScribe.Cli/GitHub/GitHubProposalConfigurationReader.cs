using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;

namespace ContractScribe.Cli;

internal sealed record GitHubProposalConfigurationSnapshot(
    string Path, byte[] Digest, GitHubPublicationConfiguration Configuration)
{
    internal bool Revalidate()
    {
        try
        {
            return SHA256.HashData(GitHubProposalConfigurationReader.ReadBounded(Path)).AsSpan().SequenceEqual(Digest);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}

internal static class GitHubProposalConfigurationReader
{
    internal const int MaximumBytes = 262_144;
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

    internal static GitHubProposalConfigurationSnapshot Read(string path, string currentDirectory)
    {
        path = System.IO.Path.GetFullPath(path, currentDirectory);
        var bytes = ReadBounded(path);
        if (bytes.Length is <= 0 or > MaximumBytes || bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new JsonException();
        _ = new UTF8Encoding(false, true).GetString(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        ValidateClosedJson(document.RootElement);
        if (!document.RootElement.TryGetProperty("transition", out var transition)
            || transition.ValueKind != JsonValueKind.String
            || transition.GetString() is not ("initial" or "same-snapshot-append" or "successor-after-merge" or "successor-after-closed-unmerged"))
            throw new JsonException();
        if (document.RootElement.TryGetProperty("terminalPredecessor", out var terminal) && terminal.ValueKind != JsonValueKind.Null
            && (terminal.ValueKind != JsonValueKind.Object
                || !terminal.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.String
                || disposition.GetString() is not ("merged" or "closed-unmerged"))) throw new JsonException();
        var configuration = document.RootElement.Deserialize<GitHubPublicationConfiguration>(Options)
            ?? throw new JsonException();
        ValidateShape(configuration);
        var snapshot = new GitHubProposalConfigurationSnapshot(path, SHA256.HashData(bytes), configuration);
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
            if (text.Length is 0 or > 4096 || text.Any(char.IsControl)) throw new JsonException();
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

    private static void ValidateShape(GitHubPublicationConfiguration configuration)
    {
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
