using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ContractScribe.HostValidation;

public static partial class DeclaredNetworkOperationInventoryEvaluator
{
    public const string EvaluatorId =
        "contractscribe-declared-network-operation-inventory-evaluator.v1";

    private const int InputByteLimit = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> DeclarationKeys = new(
        [
            "networkOperation",
            "networkOperations",
            "providerOperation",
            "providerOperations",
            "providerRequestsEnabled",
            "githubOperation",
            "githubOperations",
            "githubApiEnabled",
            "telemetryEnabled",
            "updateCheckEnabled",
            "runtimeDownloadEnabled",
            "outboundNetworkEnabled",
            "publishOperation",
            "publishOperations"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> XmlDeclarationNames = new(
        DeclarationKeys.Append("ContractScribeNetworkOperation"),
        StringComparer.OrdinalIgnoreCase);

    public static string ComputeInventoryId(SubjectSourceConfiguration source)
    {
        var inputs = ExactInputs(source)
            .Select(input => new
            {
                input.Identity.Path,
                input.Identity.Sha256,
                Roles = input.Roles
            })
            .ToArray();
        return $"operations.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(new
        {
            evaluatorId = EvaluatorId,
            inputs
        }))}";
    }

    public static bool HasDeclaredNetworkOperation(
        string root,
        SubjectSourceConfiguration source)
    {
        foreach (var input in ExactInputs(source))
        {
            var path = RepositoryPaths.ResolveConfined(root, input.Identity.Path);
            var bytes = ReadBounded(path);
            if (CanonicalJson.Sha256(bytes) != input.Identity.Sha256)
            {
                throw new ProtocolException(
                    "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
            }
            var text = StrictUtf8.GetString(bytes);
            if (input.Roles.Any(role => HasDeclaration(role, text)))
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasSyntheticDeclaration(string inputClass, string text) =>
        HasDeclaration(inputClass, text);

    private static IReadOnlyList<InventoryInput> ExactInputs(
        SubjectSourceConfiguration source)
    {
        var inputs = new Dictionary<string, MutableInventoryInput>(
            StringComparer.Ordinal);
        foreach (var identity in source.SourceAndBuildInputs)
        {
            if (IsProjectOrPackageInput(identity.Path))
            {
                Add(inputs, identity, "project-package");
            }
            if (IsStructuredConfigurationInput(identity.Path))
            {
                Add(inputs, identity, ConfigurationRole(identity.Path));
            }
        }

        Add(inputs, source.FailureRegistry, ConfigurationRole(source.FailureRegistry.Path));
        Add(inputs, source.CalibratedBounds, ConfigurationRole(source.CalibratedBounds.Path));
        Add(inputs, source.BuildRecipe, "project-package");
        Add(inputs, source.CommandContract, "command-contract");
        Add(inputs, source.ContractBaseline, ConfigurationRole(source.ContractBaseline.Path));
        Add(inputs, source.EnvironmentPolicy, "environment-policy");
        Add(inputs, source.Workflow, "workflow");
        return inputs.Values
            .OrderBy(input => input.Identity.Path, StringComparer.Ordinal)
            .Select(input => new InventoryInput(
                input.Identity,
                input.Roles.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static void Add(
        IDictionary<string, MutableInventoryInput> inputs,
        ArtifactIdentity identity,
        string role)
    {
        if (!inputs.TryGetValue(identity.Path, out var input))
        {
            input = new MutableInventoryInput(identity);
            inputs.Add(identity.Path, input);
        }
        else if (input.Identity.Sha256 != identity.Sha256)
        {
            throw new ProtocolException(
                "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
        }
        input.Roles.Add(role);
    }

    private static bool IsProjectOrPackageInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";

    private static bool IsStructuredConfigurationInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".json" or ".jsonc" or ".config" or ".toml" or ".yaml" or ".yml";

    private static string ConfigurationRole(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "configuration-json",
            ".jsonc" => "configuration-jsonc",
            ".config" => "configuration-xml",
            ".toml" or ".yaml" or ".yml" => "configuration-text",
            _ => throw new ProtocolException(
                "HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE")
        };

    private static byte[] ReadBounded(string path)
    {
        if (!File.Exists(path))
        {
            throw new ProtocolException(
                "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
        }
        var length = new FileInfo(path).Length;
        if (length > InputByteLimit)
        {
            throw new ProtocolException(
                "HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE");
        }
        return File.ReadAllBytes(path);
    }

    private static bool HasDeclaration(string inputClass, string text) =>
        inputClass switch
        {
            "project-package" => HasProjectOrPackageDeclaration(text),
            "configuration-json" => HasConfigurationDeclaration(
                text,
                JsonCommentHandling.Disallow),
            "configuration-jsonc" => HasConfigurationDeclaration(
                text,
                JsonCommentHandling.Skip),
            "configuration-xml" => HasProjectOrPackageDeclaration(text),
            "configuration-text" => HasTextConfigurationDeclaration(text),
            "command-contract" => HasCommandDeclaration(text),
            "environment-policy" => HasEnvironmentPolicyDeclaration(text),
            "workflow" => HasWorkflowDeclaration(text),
            _ => throw new ProtocolException(
                "HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE")
        };

    private static bool HasProjectOrPackageDeclaration(string text)
    {
        using var reader = XmlReader.Create(
            new StringReader(text),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
        var document = XDocument.Load(reader, LoadOptions.None);
        return document.Descendants().Any(element =>
        {
            var name = element.Name.LocalName;
            if (name is "Exec" or "UsingTask")
            {
                return true;
            }
            if (name is "PackageReference" or "PackageVersion" or "PackageDownload")
            {
                var package = element.Attribute("Include")?.Value
                    ?? element.Attribute("Update")?.Value
                    ?? string.Empty;
                return NetworkPackage().IsMatch(package);
            }
            return XmlDeclarationNames.Contains(name)
                && IsAffirmative(element.Value);
        });
    }

    private static bool HasConfigurationDeclaration(
        string text,
        JsonCommentHandling commentHandling)
    {
        using var document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = commentHandling,
                MaxDepth = 64
            });
        return HasConfigurationDeclaration(document.RootElement);
    }

    private static bool HasConfigurationDeclaration(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject().Any(property =>
                (DeclarationKeys.Contains(property.Name)
                    && IsAffirmative(property.Value))
                || HasConfigurationDeclaration(property.Value));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasConfigurationDeclaration);
        }
        return false;
    }

    private static bool HasCommandDeclaration(string text)
    {
        using var document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        return document.RootElement
            .EnumerateDescendants()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .Any(value => NetworkCommandArgument().IsMatch(value));
    }

    private static bool HasEnvironmentPolicyDeclaration(string text) =>
        Lines(text)
            .Where(line => !NegativeDeclaration().IsMatch(line))
            .Any(line => AffirmativeProductOperation().IsMatch(line));

    private static bool HasTextConfigurationDeclaration(string text) =>
        Lines(text).Any(line =>
            !NegativeDeclaration().IsMatch(line)
            && TextConfigurationDeclaration().IsMatch(line));

    private static bool HasWorkflowDeclaration(string text) =>
        Lines(text).Any(line =>
            !NegativeDeclaration().IsMatch(line)
            && (ContractScribeNetworkCommand().IsMatch(line)
                || ContractScribeNetworkEnvironment().IsMatch(line)));

    private static IEnumerable<string> Lines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim());

    private static bool IsAffirmative(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !NegativeValue().IsMatch(value.Trim());

    private static bool IsAffirmative(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => IsAffirmative(value.GetString()!),
            JsonValueKind.Number => !value.TryGetInt64(out var number) || number != 0,
            JsonValueKind.Array => value.EnumerateArray().Any(IsAffirmative),
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => false
        };

    [GeneratedRegex(
        @"(?i)^(?:System\.Net(?:\.|$)|Microsoft\.Extensions\.Http$|Refit(?:\.|$)|RestSharp(?:\.|$)|Flurl(?:\.|$)|Octokit(?:\.|$)|Azure\.AI\.OpenAI$|OpenAI(?:\.|$)|Anthropic(?:\.|$)|AWSSDK\.|Google\.Cloud\.|OpenTelemetry\.Exporter\.|Microsoft\.ApplicationInsights$|Sentry(?:\.|$)|Datadog(?:\.|$)|NewRelic(?:\.|$))")]
    private static partial Regex NetworkPackage();

    [GeneratedRegex(
        @"(?i)^--(?:provider|publish|telemetry|check-updates|update-check|download|runtime-download|github-api)(?:$|=)")]
    private static partial Regex NetworkCommandArgument();

    [GeneratedRegex(
        @"(?i)\b(?:no|not|never|without|disabled|false|none|must\s+not|does\s+not|do\s+not|cannot|can't|won't)\b")]
    private static partial Regex NegativeDeclaration();

    [GeneratedRegex(
        @"(?i)^(?:|0|false|no|none|null|disabled|off|never|not-configured)$")]
    private static partial Regex NegativeValue();

    [GeneratedRegex(
        @"(?i)\b(?:ContractScribe|the\s+(?:production\s+)?host)\b.{0,80}\b(?:initiate|call|connect|send|post|upload|download|publish|emit|check)\w*\b.{0,80}\b(?:network|internet|http|provider|github\s+api|telemetry|updates?|runtime\s+download)\b")]
    private static partial Regex AffirmativeProductOperation();

    [GeneratedRegex(
        @"(?i)\brun\s*:.{0,160}\b(?:contractscribe|ContractScribe\.dll)\b.{0,160}--(?:provider|publish|telemetry|check-updates|update-check|download|runtime-download|github-api)(?:\s|=|$)")]
    private static partial Regex ContractScribeNetworkCommand();

    [GeneratedRegex(
        @"(?i)\bCONTRACTSCRIBE_[A-Z0-9_]*(?:PROVIDER|PUBLISH|TELEMETRY|UPDATE|DOWNLOAD|NETWORK|GITHUB)[A-Z0-9_]*\s*:\s*(?!false\b|no\b|none\b|off\b|disabled\b|0\b).+")]
    private static partial Regex ContractScribeNetworkEnvironment();

    [GeneratedRegex(
        @"(?i)^(?:networkOperation|networkOperations|providerOperation|providerOperations|providerRequestsEnabled|githubOperation|githubOperations|githubApiEnabled|telemetryEnabled|updateCheckEnabled|runtimeDownloadEnabled|outboundNetworkEnabled|publishOperation|publishOperations)\s*(?:=|:)\s*(?!false\b|no\b|none\b|off\b|disabled\b|0\b).+")]
    private static partial Regex TextConfigurationDeclaration();

    private sealed record InventoryInput(
        ArtifactIdentity Identity,
        IReadOnlyList<string> Roles);

    private sealed class MutableInventoryInput(ArtifactIdentity identity)
    {
        public ArtifactIdentity Identity { get; } = identity;

        public HashSet<string> Roles { get; } = new(StringComparer.Ordinal);
    }
}

internal static class JsonElementTraversal
{
    public static IEnumerable<JsonElement> EnumerateDescendants(
        this JsonElement element)
    {
        yield return element;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var descendant in property.Value.EnumerateDescendants())
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in item.EnumerateDescendants())
                {
                    yield return descendant;
                }
            }
        }
    }
}
