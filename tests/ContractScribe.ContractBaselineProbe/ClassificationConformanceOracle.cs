using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace ContractScribe.ContractBaselineProbe;

public sealed class ClassificationConformanceOracle
{
    private static readonly string[] RecordTypes =
    [
        "TargetClassification",
        "ComponentClassification",
        "RelationObservation",
        "UnresolvedClassification",
    ];

    private readonly IReadOnlyDictionary<string, JsonSchema> schemas;
    private readonly IReadOnlyDictionary<string, JsonElement> registryEntries;
    private readonly IReadOnlyDictionary<string, HashSet<string>> registryIds;

    private ClassificationConformanceOracle(
        IReadOnlyDictionary<string, JsonSchema> schemas,
        IReadOnlyDictionary<string, JsonElement> registryEntries,
        IReadOnlyDictionary<string, HashSet<string>> registryIds)
    {
        this.schemas = schemas;
        this.registryEntries = registryEntries;
        this.registryIds = registryIds;
    }

    public static ClassificationConformanceOracle Load(string repositoryRoot)
    {
        var taxonomyRoot = Path.Combine(
            repositoryRoot,
            "schemas",
            "symbol-evidence-taxonomy");
        var schemaRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(
            taxonomyRoot,
            "v1.schema.json")))!.AsObject();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TargetClassification"] = "targetClassification",
            ["ComponentClassification"] = "componentClassification",
            ["RelationObservation"] = "relationObservation",
            ["UnresolvedClassification"] = "unresolvedClassification",
        };
        var schemas = definitions.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var schema = schemaRoot["$defs"]![pair.Value]!
                    .DeepClone()
                    .AsObject();
                schema["$schema"] =
                    "https://json-schema.org/draft/2020-12/schema";
                schema["$defs"] = schemaRoot["$defs"]!.DeepClone();
                return JsonSchema.FromText(schema.ToJsonString());
            },
            StringComparer.Ordinal);

        using var registryDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(taxonomyRoot, "v1.registry.json")));
        var sections = registryDocument.RootElement.GetProperty("sections");
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var section in sections.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var sectionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in section.Value.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                sectionIds.Add(id);
                entries.Add(id, entry.Clone());
            }

            ids.Add(section.Name, sectionIds);
        }

        return new ClassificationConformanceOracle(schemas, entries, ids);
    }

    public bool IsValidRecord(JsonElement record) =>
        TryValidateRecord(record, out _);

    public bool TryValidateSet(
        string targetProfile,
        IReadOnlyList<JsonElement> records,
        IReadOnlyDictionary<string, string>? independentEndpointKinds,
        out string? error)
    {
        if (!Known("targetProfiles", targetProfile))
        {
            error = $"unknown closed target profile: {targetProfile}";
            return false;
        }

        foreach (var record in records)
        {
            if (!TryValidateRecord(record, out error))
            {
                return false;
            }
        }

        var targets = new Dictionary<string, (string Kind, string Status)>(
            StringComparer.Ordinal);
        var endpointKinds = independentEndpointKinds is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                independentEndpointKinds,
                StringComparer.Ordinal);
        if (endpointKinds.Any(pair =>
            !Known("primaryKinds", pair.Value)))
        {
            error = "independent endpoint kind is outside the closed registry";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var recordType = record.GetProperty("recordType").GetString()!;
            var key = RecordKey(record);
            if (!keys.Add(key))
            {
                error = $"duplicate classification key: {key}";
                return false;
            }

            if (recordType == "TargetClassification")
            {
                var symbolKey = SymbolKey(record.GetProperty("symbolRef"));
                var kind = record.GetProperty("primaryKind").GetString()!;
                var status = record.GetProperty("supportStatus").GetString()!;
                if (!targets.TryAdd(symbolKey, (kind, status))
                    || endpointKinds.TryGetValue(symbolKey, out var knownKind)
                        && knownKind != kind)
                {
                    error = $"conflicting target endpoint kind: {symbolKey}";
                    return false;
                }

                endpointKinds[symbolKey] = kind;
            }
        }

        foreach (var record in records)
        {
            var recordType = record.GetProperty("recordType").GetString();
            if (recordType == "ComponentClassification")
            {
                var parentKey = SymbolKey(
                    record.GetProperty("parentSymbolRef"));
                if (!targets.TryGetValue(parentKey, out var parent)
                    || parent.Status != "support.supported"
                    || !Contains(
                        registryEntries[
                            record.GetProperty("componentKind").GetString()!],
                        "parentKinds",
                        parent.Kind))
                {
                    error = $"component parent domain mismatch: {parentKey}";
                    return false;
                }
            }
            else if (recordType == "RelationObservation")
            {
                var relationEntry = registryEntries[
                    record.GetProperty("relationKind").GetString()!];
                var sourceKey = SymbolKey(
                    record.GetProperty("sourceSymbolRef"));
                var targetKey = SymbolKey(
                    record.GetProperty("targetSymbolRef"));
                if (!endpointKinds.TryGetValue(sourceKey, out var sourceKind)
                    || !endpointKinds.TryGetValue(targetKey, out var targetKind)
                    || !Contains(
                        relationEntry,
                        "sourceDomain",
                        sourceKind)
                    || !Contains(
                        relationEntry,
                        "targetDomain",
                        targetKind))
                {
                    error =
                        $"relation endpoint domain mismatch: {sourceKey} -> {targetKey}";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private bool TryValidateRecord(JsonElement record, out string? error)
    {
        if (record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("recordType", out var type)
            || type.GetString() is not { } recordType
            || !RecordTypes.Contains(recordType, StringComparer.Ordinal)
            || !schemas[recordType].Evaluate(record).IsValid)
        {
            error = "record does not match the closed schema";
            return false;
        }

        var valid = recordType switch
        {
            "TargetClassification" =>
                Known(
                    "primaryKinds",
                    record.GetProperty("primaryKind").GetString())
                && record.GetProperty("traits")
                    .EnumerateArray()
                    .All(value => Known("traits", value.GetString()))
                && IsValidStatusAndSkip(
                    record,
                    recordType,
                    record.GetProperty("primaryKind").GetString()!),
            "ComponentClassification" =>
                Known(
                    "componentKinds",
                    record.GetProperty("componentKind").GetString())
                && IsValidComponentIdentity(
                    record.GetProperty("componentKind").GetString()!,
                    record.GetProperty("identity").GetString())
                && IsValidStatusAndSkip(
                    record,
                    recordType,
                    record.GetProperty("componentKind").GetString()!),
            "RelationObservation" =>
                Known(
                    "relationKinds",
                    record.GetProperty("relationKind").GetString()),
            "UnresolvedClassification" => IsValidUnresolved(record),
            _ => false,
        };
        error = valid ? null : $"semantic oracle rejected {recordType}";
        return valid;
    }

    private bool IsValidStatusAndSkip(
        JsonElement record,
        string recordType,
        string classifiedId)
    {
        var status = record.GetProperty("supportStatus").GetString();
        var origin = record.GetProperty("origin").GetString();
        if (!Known("supportStatuses", status)
            || !Known("origins", origin)
            || !AllowsRecord(registryEntries[status!], recordType)
            || !AllowsRecord(registryEntries[origin!], recordType)
            || !Contains(
                registryEntries[classifiedId],
                "allowedSupportStatuses",
                status!))
        {
            return false;
        }

        if ((classifiedId is "symbol.unknown" or "component.unknown")
            != (status == "support.unsupported"))
        {
            return false;
        }

        if (registryEntries[classifiedId].TryGetProperty(
                "requiredOrigin",
                out var requiredOrigin)
            && origin != requiredOrigin.GetString())
        {
            return false;
        }

        if (status == "support.supported")
        {
            return !record.TryGetProperty("skipReason", out _);
        }

        if (!record.TryGetProperty("skipReason", out var skip)
            || !Known("skipReasons", skip.GetString())
            || !AllowsRecord(registryEntries[skip.GetString()!], recordType)
            || !Contains(
                registryEntries[skip.GetString()!],
                "allowedSupportStatuses",
                status!))
        {
            return false;
        }

        if (origin == "origin.unknown"
            && (status != "support.unavailable-context"
                || skip.GetString() is not
                    ("skip.unavailable.generated-provenance"
                        or "skip.unavailable.semantic-context")))
        {
            return false;
        }

        if (origin == "origin.mixed"
            && !((status == "support.ambiguous"
                    && skip.GetString() is
                        "skip.ambiguous.mixed-origin"
                        or "skip.ambiguous.partial-declaration")
                || (status == "support.unavailable-context"
                    && skip.GetString() is
                        "skip.unavailable.generated-provenance"
                        or "skip.unavailable.semantic-context")))
        {
            return false;
        }

        return !registryEntries[classifiedId].TryGetProperty(
                "requiredSkip",
                out var requiredSkip)
            || skip.GetString() == requiredSkip.GetString();
    }

    private bool IsValidUnresolved(JsonElement record)
    {
        var origin = record.GetProperty("origin").GetString();
        var skip = record.GetProperty("skipReason").GetString();
        return record.GetProperty("supportStatus").GetString()
                == "support.unavailable-context"
            && Known("origins", origin)
            && Known("skipReasons", skip)
            && AllowsRecord(registryEntries[origin!], "UnresolvedClassification")
            && AllowsRecord(registryEntries[skip!], "UnresolvedClassification")
            && (origin != "origin.unknown"
                || skip is "skip.unavailable.generated-provenance"
                    or "skip.unavailable.semantic-context")
            && IsValidCandidateLocator(
                record.GetProperty("candidateLocator"));
    }

    private bool Known(string section, string? id) =>
        id is not null && registryIds[section].Contains(id);

    private static bool AllowsRecord(JsonElement entry, string recordType) =>
        Contains(entry, "recordTypes", recordType);

    private static bool Contains(
        JsonElement entry,
        string propertyName,
        string value) =>
        entry.TryGetProperty(propertyName, out var values)
        && values.EnumerateArray()
            .Any(candidate => candidate.GetString() == value);

    private static bool IsValidComponentIdentity(
        string kind,
        string? identity) =>
        identity is not null
        && kind switch
        {
            "component.parameter" =>
                Regex.IsMatch(identity, "^parameter/[0-9]+$"),
            "component.type-parameter" =>
                Regex.IsMatch(identity, "^type-parameter/[0-9]+$"),
            "component.return" => identity == "return",
            "component.value" => identity == "value",
            "component.accessor.get" => identity == "accessor/get",
            "component.accessor.set" => identity == "accessor/set",
            "component.accessor.init" => identity == "accessor/init",
            "component.accessor.add" => identity == "accessor/add",
            "component.accessor.remove" => identity == "accessor/remove",
            "component.backing-field" => identity == "backing-field",
            "component.synthesized.record-positional-property" =>
                Regex.IsMatch(
                    identity,
                    "^synthesized/record-positional-property/[0-9]+$"),
            "component.synthesized.implicit-constructor" =>
                identity == "synthesized/implicit-constructor",
            "component.synthesized.record-copy-constructor" =>
                identity == "synthesized/record-copy-constructor",
            "component.synthesized.delegate-invoke" =>
                identity == "synthesized/delegate-invoke",
            "component.synthesized.delegate-begin-invoke" =>
                identity == "synthesized/delegate-begin-invoke",
            "component.synthesized.delegate-end-invoke" =>
                identity == "synthesized/delegate-end-invoke",
            "component.unknown" =>
                Regex.IsMatch(identity, "^unknown/[0-9]+$"),
            _ => false,
        };

    private static bool IsValidCandidateLocator(JsonElement locator)
    {
        var variants = new[]
        {
            "repository",
            "generatedSource",
            "toolGenerated",
            "synthetic",
        }.Where(name => locator.TryGetProperty(name, out _)).ToArray();
        return variants.Length == 1
            && variants[0] switch
            {
                "repository" =>
                    IsLexicalRepositoryPath(
                        locator.GetProperty("repository")
                            .GetProperty("path")
                            .GetString())
                    && HasValidOptionalSpan(
                        locator.GetProperty("repository")),
                "generatedSource" =>
                    IsGeneratedId(
                        locator.GetProperty("generatedSource")
                            .GetProperty("generatorId")
                            .GetString(),
                        "sgp.")
                    && IsGeneratedId(
                        locator.GetProperty("generatedSource")
                            .GetProperty("hintNameId")
                            .GetString(),
                        "sgo.")
                    && HasValidOptionalSpan(
                        locator.GetProperty("generatedSource")),
                "toolGenerated" =>
                    IsGeneratedId(
                        locator.GetProperty("toolGenerated")
                            .GetProperty("producerId")
                            .GetString(),
                        "tgp.")
                    && IsGeneratedId(
                        locator.GetProperty("toolGenerated")
                            .GetProperty("outputId")
                            .GetString(),
                        "tgo.")
                    && HasValidOptionalSpan(
                        locator.GetProperty("toolGenerated")),
                "synthetic" =>
                    Regex.IsMatch(
                        locator.GetProperty("synthetic")
                            .GetProperty("fixtureId")
                            .GetString()
                            ?? string.Empty,
                        "^[a-z0-9][a-z0-9._-]{0,127}$"),
                _ => false,
            };
    }

    private static bool HasValidOptionalSpan(JsonElement locator)
    {
        if (!locator.TryGetProperty("span", out var span))
        {
            return true;
        }

        return span.GetProperty("start").GetInt32()
            <= span.GetProperty("end").GetInt32();
    }

    private static bool IsLexicalRepositoryPath(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Contains('\0')
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || Regex.IsMatch(path, "^[A-Za-z]:"))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/')
            .Split('/')
            .Where(segment => segment is not "" and not ".")
            .ToArray();
        return normalized.Length > 0
            && normalized.All(segment => segment != "..");
    }

    private static bool IsGeneratedId(string? value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value[prefix.Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RecordKey(JsonElement record) =>
        record.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" =>
                "0|" + SymbolKey(record.GetProperty("symbolRef")),
            "ComponentClassification" =>
                "1|" + SymbolKey(record.GetProperty("parentSymbolRef"))
                + "|" + record.GetProperty("componentKind").GetString()
                + "|" + record.GetProperty("identity").GetString(),
            "RelationObservation" =>
                "2|" + SymbolKey(record.GetProperty("sourceSymbolRef"))
                + "|" + record.GetProperty("relationKind").GetString()
                + "|" + SymbolKey(record.GetProperty("targetSymbolRef")),
            "UnresolvedClassification" =>
                "3|"
                + record.GetProperty("compilationContextRef").GetString()
                + "|"
                + CandidateLocatorKey(
                    record.GetProperty("candidateLocator")),
            _ => throw new InvalidOperationException(),
        };

    private static string CandidateLocatorKey(JsonElement locator)
    {
        static string SpanKey(JsonElement value)
        {
            if (!value.TryGetProperty("span", out var span))
            {
                return "0";
            }

            return "1|"
                + span.GetProperty("start").GetInt32()
                + "|"
                + span.GetProperty("end").GetInt32();
        }

        if (locator.TryGetProperty("repository", out var repository))
        {
            return "repository|"
                + repository.GetProperty("path").GetString()
                + "|"
                + SpanKey(repository);
        }

        if (locator.TryGetProperty("generatedSource", out var generatedSource))
        {
            return "generatedSource|"
                + generatedSource.GetProperty("generatorId").GetString()
                + "|"
                + generatedSource.GetProperty("hintNameId").GetString()
                + "|"
                + SpanKey(generatedSource);
        }

        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return "toolGenerated|"
                + toolGenerated.GetProperty("producerId").GetString()
                + "|"
                + toolGenerated.GetProperty("outputId").GetString()
                + "|"
                + SpanKey(toolGenerated);
        }

        return "synthetic|"
            + locator.GetProperty("synthetic")
                .GetProperty("fixtureId")
                .GetString();
    }

    private static string SymbolKey(JsonElement symbolRef) =>
        symbolRef.GetProperty("compilationContextRef").GetString()
        + "|"
        + symbolRef.GetProperty("documentationCommentId").GetString();
}
