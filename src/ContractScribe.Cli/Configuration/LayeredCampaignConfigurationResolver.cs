using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Cli;

// Resolves the layered consumer configuration into the complete campaign
// execution document: payload defaults < downstream file < invocation override.
// The resolved document is validated only by CampaignConfiguration.Parse; this
// resolver performs layer-shape checks and ordered overlay application but adds
// no second semantic validator and no environment, include, or reload behavior.
internal static class LayeredCampaignConfigurationResolver
{
    internal const int MaximumSourceBytes = 262_144;
    private const int MaximumLayerDepth = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumLayerDepth,
    };

    internal static string PayloadDefaultsPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "defaults.json");

    internal static CampaignResolvedConfigurationSnapshot Resolve(
        CampaignCommandArguments arguments,
        string currentDirectory) =>
        Resolve(arguments, currentDirectory, PayloadDefaultsPath, CliBuildIdentity.Current);

    internal static CampaignResolvedConfigurationSnapshot Resolve(
        CampaignCommandArguments arguments,
        string currentDirectory,
        string defaultsPath,
        CliBuildIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(arguments.CampaignLineage);

        var sources = ImmutableArray.CreateBuilder<CampaignConfigurationSource>();
        var defaults = ReadSource(defaultsPath, currentDirectory: null);
        sources.Add(defaults);
        var resolved = ParseDefaults(defaults.Content);

        if (arguments.Configuration is { } downstreamPath)
        {
            var downstream = ReadSource(downstreamPath, currentDirectory);
            sources.Add(downstream);
            ApplyLayer(resolved, ReadLayer(downstream.Content));
        }
        if (arguments.ConfigurationOverride is { } overridePath)
        {
            var overlay = ReadSource(overridePath, currentDirectory);
            sources.Add(overlay);
            ApplyLayer(resolved, ReadLayer(overlay.Content));
        }

        var planning = (JsonObject?)resolved["planning"]
            ?? throw new CampaignConfigurationException();
        planning.Insert(0, "campaignLineage", arguments.CampaignLineage);
        planning["productContractRevisionSha256"] =
            CampaignCommandRunner.ProductRevisionSha256(identity);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(resolved);
        return new CampaignResolvedConfigurationSnapshot(
            sources.ToImmutable(),
            CampaignConfiguration.Parse(bytes));
    }

    private static JsonObject ParseDefaults(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                throw new CampaignConfigurationException();
            }
            _ = StrictUtf8.GetString(bytes);
            var node = JsonNode.Parse(bytes, documentOptions: DocumentOptions);
            return node is JsonObject root
                && root["planning"] is JsonObject
                && root["budgets"] is JsonObject
                ? root
                : throw new CampaignConfigurationException();
        }
        catch (CampaignConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new CampaignConfigurationException();
        }
    }

    private static JsonElement ReadLayer(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                throw new CampaignConfigurationException();
            }
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : throw new CampaignConfigurationException();
        }
        catch (CampaignConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new CampaignConfigurationException();
        }
    }

    internal static CampaignConfigurationSource ReadSource(
        string value,
        string? currentDirectory)
    {
        try
        {
            var lexical = currentDirectory is null
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, currentDirectory);
            var path = CliPreflight.ResolveExistingPath(lexical);
            if (!CliPreflight.IsRegularFileNoFollow(path))
            {
                throw new CampaignConfigurationException();
            }
            var infoBefore = new FileInfo(path);
            if (infoBefore.Length is <= 0 or > MaximumSourceBytes)
            {
                throw new CampaignConfigurationException();
            }
            var bytes = File.ReadAllBytes(path);
            var infoAfter = new FileInfo(path);
            if (infoBefore.Length != bytes.LongLength
                || infoBefore.Length != infoAfter.Length
                || infoBefore.LastWriteTimeUtc != infoAfter.LastWriteTimeUtc)
            {
                throw new CampaignConfigurationException();
            }
            return new CampaignConfigurationSource(
                path,
                bytes.LongLength,
                infoAfter.LastWriteTimeUtc,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes);
        }
        catch (CampaignConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (CliPreflight.IsPathFailure(exception))
        {
            throw new CampaignConfigurationException();
        }
    }

    private static void ApplyLayer(JsonObject target, JsonElement layer)
    {
        if (RequireVersion(layer) != 1)
        {
            throw new CampaignConfigurationException();
        }
        ApplyObject(target, ConsumerConfigurationNode.Root, layer, skipVersion: true);
    }

    private static int RequireVersion(JsonElement layer)
    {
        var found = false;
        var version = 0;
        foreach (var property in layer.EnumerateObject())
        {
            if (property.NameEquals("consumerConfigurationVersion"))
            {
                if (found
                    || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out version))
                {
                    throw new CampaignConfigurationException();
                }
                found = true;
            }
        }
        return found ? version : throw new CampaignConfigurationException();
    }

    private static void ApplyObject(
        JsonObject target,
        ConsumerConfigurationNode schema,
        JsonElement layer,
        bool skipVersion)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in layer.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new CampaignConfigurationException();
            }
            if (skipVersion && property.NameEquals("consumerConfigurationVersion"))
            {
                continue;
            }
            var child = FindChild(schema, property.Name)
                ?? throw new CampaignConfigurationException();
            var value = property.Value;
            if (child.Leaf is { } leaf)
            {
                CheckLeaf(value, leaf, child.Nullable);
                target[child.Name] = JsonNode.Parse(value.GetRawText());
                continue;
            }
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!child.Nullable)
                {
                    throw new CampaignConfigurationException();
                }
                target[child.Name] = null;
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new CampaignConfigurationException();
            }
            var node = target[child.Name] as JsonObject ?? new JsonObject();
            ApplyObject(node, child, value, skipVersion: false);
            target[child.Name] = node;
        }
    }

    private static ConsumerConfigurationNode? FindChild(
        ConsumerConfigurationNode schema,
        string name)
    {
        foreach (var child in schema.Children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }
        return null;
    }

    private static void CheckLeaf(JsonElement value, ConsumerValueKind kind, bool nullable)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullable)
            {
                return;
            }
            throw new CampaignConfigurationException();
        }
        var valid = kind switch
        {
            ConsumerValueKind.Integer => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out _),
            ConsumerValueKind.Text => value.ValueKind == JsonValueKind.String,
            ConsumerValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ConsumerValueKind.Array => value.ValueKind == JsonValueKind.Array,
            _ => false,
        };
        if (!valid)
        {
            throw new CampaignConfigurationException();
        }
    }
}
