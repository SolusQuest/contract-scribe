using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractScribe.Core;

namespace ContractScribe.Cli;

// Stable consumer identity only. The CLI derives snapshot, batch and publication
// authority after authenticating the coordination ref and current target.
internal sealed record GitHubCampaignConfiguration(
    [property: JsonPropertyName("githubCampaignVersion")] int Version,
    string CampaignLineage,
    [property: JsonPropertyName("github")] GitHubCampaignDestination GitHub);

internal sealed record GitHubCampaignDestination(string RepositoryOwner, string RepositoryName, string TargetRef);

internal sealed record GitHubCampaignConfigurationSnapshot(
    string Path, byte[] Digest, GitHubCampaignConfiguration Configuration)
{
    internal bool Revalidate()
    {
        try
        {
            return SHA256.HashData(GitHubCampaignConfigurationReader.ReadBounded(Path)).AsSpan().SequenceEqual(Digest);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}

internal static class GitHubCampaignConfigurationReader
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

    internal static GitHubCampaignConfigurationSnapshot Read(string path, string currentDirectory)
    {
        path = System.IO.Path.GetFullPath(path, currentDirectory);
        var bytes = ReadBounded(path);
        if (bytes.Length is <= 0 or > MaximumBytes || bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new JsonException();
        _ = new UTF8Encoding(false, true).GetString(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        ValidateClosedJson(document.RootElement);
        if (!document.RootElement.TryGetProperty("githubCampaignVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != 1)
            throw new JsonException();
        var request = document.RootElement.Deserialize<GitHubCampaignConfiguration>(Options)
            ?? throw new JsonException();
        ValidateShape(request);
        var snapshot = new GitHubCampaignConfigurationSnapshot(path, SHA256.HashData(bytes), request);
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

    private static void ValidateShape(GitHubCampaignConfiguration request)
    {
        if (request.Version != 1 || !GitHubPublicationFactory.IsCampaignLineage(request.CampaignLineage))
            throw new JsonException();
        var configuration = request.GitHub;
        if (!GitHubPublicationFactory.IsRefName(configuration.TargetRef)
            || !configuration.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new JsonException();
        foreach (var part in new[] { configuration.RepositoryOwner, configuration.RepositoryName })
            if (part is null || part.Length is 0 or > 100
                || part is "." or ".." || !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new JsonException();
    }
}
