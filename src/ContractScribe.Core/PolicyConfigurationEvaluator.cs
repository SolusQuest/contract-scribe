using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class PolicyConfigurationEvaluator
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static PolicyParseOutcome Parse(
        byte[]? payload,
        CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            return Failure("policy.input.missing-document");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasUtf8Bom(payload))
            {
                return Failure("policy.document.bom-not-allowed");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                return Failure("policy.document.invalid-encoding");
            }

            cancellationToken.ThrowIfCancellationRequested();
            JsonDocument parsed;
            try
            {
                var duplicatePointer = FindDuplicatePropertyPointer(payload);
                if (duplicatePointer is not null)
                {
                    return Failure(
                        "policy.document.duplicate-property",
                        duplicatePointer);
                }

                parsed = JsonDocument.Parse(
                    text,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                    });
            }
            catch (JsonException)
            {
                return Failure("policy.document.invalid-json");
            }

            using (parsed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = parsed.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("schemaVersion", out var version)
                    && version.ValueKind == JsonValueKind.Number
                    && IsIntegerOtherThanOne(version.GetRawText()))
                {
                    return Failure(
                        "policy.schema.unsupported-version",
                        "/schemaVersion");
                }

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("schemaVersion", out version)
                    && IsIntegerOne(version))
                {
                    if (!root.TryGetProperty("targetProfile", out var profile))
                    {
                        return Failure(
                            "policy.target-profile.required",
                            "/targetProfile");
                    }

                    if (profile.ValueKind != JsonValueKind.String
                        || profile.GetString() is not (
                            "profile.external-api"
                            or "profile.assembly-visible"))
                    {
                        return Failure(
                            "policy.target-profile.invalid",
                            "/targetProfile");
                    }
                }

                var schemaFailures = CollectSchemaFailures(root);
                if (schemaFailures.Count > 0)
                {
                    var first = schemaFailures
                        .OrderBy(failure => failure.Pointer, StringComparer.Ordinal)
                        .ThenBy(failure => failure.Keyword, StringComparer.Ordinal)
                        .First();
                    return Failure(
                        "policy.schema.invalid-document",
                        first.Pointer,
                        first.Keyword);
                }

                var semanticFailure = ValidateSemantics(root, cancellationToken);
                if (semanticFailure is not null)
                {
                    return PolicyParseOutcome.Failure(semanticFailure);
                }

                var targetProfile = root.GetProperty("targetProfile").GetString() switch
                {
                    "profile.external-api" => TargetProfile.ExternalApi,
                    "profile.assembly-visible" => TargetProfile.AssemblyVisible,
                    _ => throw new InvalidOperationException(
                        "Schema-valid Policy contains an unknown target profile."),
                };
                var defaultExpectation = ParseExpectation(
                    root.GetProperty("defaultDecision").GetString()!);
                var rules = ParseRules(root, cancellationToken);
                return PolicyParseOutcome.Success(
                    new PolicyDocumentV1(
                        targetProfile,
                        defaultExpectation,
                        rules));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PolicyParseOutcome.Cancelled();
        }
    }

    public static PolicyEvaluationOutcome Evaluate(
        PolicyDocumentV1 document,
        IEnumerable<PolicyContributionInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inputs);

        try
        {
            var byKey = new Dictionary<string, PolicyContribution>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input is null)
                {
                    return EvaluationFailure(
                        "policy.input.invalid-contribution",
                        "/sourcePath");
                }

                var normalized = NormalizeInput(input);
                if (normalized.Failure is not null)
                {
                    return PolicyEvaluationOutcome.Failure(normalized.Failure);
                }

                var contribution = Resolve(document, normalized, cancellationToken);
                var key = ContributionKey(contribution);
                if (byKey.TryGetValue(key, out var existing))
                {
                    if (!Equals(existing, contribution))
                    {
                        return EvaluationFailure(
                            "policy.input.invalid-contribution",
                            "/projectPath");
                    }

                    continue;
                }

                byKey.Add(key, contribution);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var ordered = byKey
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToImmutableArray();
            return PolicyEvaluationOutcome.Success(
                new PolicyContributionSet(document.TargetProfile, ordered));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PolicyEvaluationOutcome.Cancelled();
        }
    }

    public static PolicyEvaluationOutcome Evaluate(
        PolicyDocumentV1 document,
        PolicyContributionInput input,
        CancellationToken cancellationToken = default) =>
        Evaluate(document, [input], cancellationToken);

    private static ImmutableArray<PolicyRuleV1> ParseRules(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("rules", out var rules))
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<PolicyRuleV1>(rules.GetArrayLength());
        foreach (var rule in rules.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(new PolicyRuleV1(
                rule.GetProperty("id").GetString()!,
                rule.GetProperty("priority").GetInt32(),
                ParseExpectation(rule.GetProperty("decision").GetString()!),
                ParseSelector(rule, "projectPaths"),
                ParseSelector(rule, "sourcePaths")));
        }

        return builder.MoveToImmutable();
    }

    private static PolicyPathSelectorV1? ParseSelector(
        JsonElement rule,
        string propertyName)
    {
        if (!rule.TryGetProperty(propertyName, out var selector))
        {
            return null;
        }

        return new PolicyPathSelectorV1(
            ReadPatterns(selector, "include"),
            ReadPatterns(selector, "exclude"));
    }

    private static ImmutableArray<string> ReadPatterns(
        JsonElement selector,
        string propertyName) =>
        selector.TryGetProperty(propertyName, out var patterns)
            ? patterns.EnumerateArray()
                .Select(pattern => pattern.GetString()!)
                .ToImmutableArray()
            : [];

    private static PolicyExpectation ParseExpectation(string value) => value switch
    {
        "required" => PolicyExpectation.Required,
        "optional" => PolicyExpectation.Optional,
        "forbidden" => PolicyExpectation.Forbidden,
        _ => throw new InvalidOperationException(
            "Schema-valid Policy contains an unknown expectation."),
    };

    private static PolicyContribution Resolve(
        PolicyDocumentV1 document,
        NormalizedContributionInput input,
        CancellationToken cancellationToken)
    {
        PolicyRuleV1? selectedRule = null;
        var selectedPriority = -1;
        foreach (var rule in document.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.Priority > selectedPriority && RuleApplies(rule, input))
            {
                selectedRule = rule;
                selectedPriority = rule.Priority;
            }
        }

        var expectation = selectedRule?.Expectation ?? document.DefaultExpectation;
        return input.GeneratedOutput is { } generated
            ? new GeneratedPolicyContribution(
                input.ProjectPath,
                generated,
                expectation,
                selectedRule?.Id)
            : new RepositoryPolicyContribution(
                input.ProjectPath,
                input.SourcePath!,
                expectation,
                selectedRule?.Id);
    }

    private static bool RuleApplies(
        PolicyRuleV1 rule,
        NormalizedContributionInput input) =>
        (rule.ProjectPaths is null
            || SelectorAccepts(rule.ProjectPaths, input.ProjectPath))
        && (rule.SourcePaths is null
            || input.SourcePath is not null
                && SelectorAccepts(rule.SourcePaths, input.SourcePath));

    private static bool SelectorAccepts(
        PolicyPathSelectorV1 selector,
        string path)
    {
        var included = selector.Include.IsDefaultOrEmpty
            || selector.Include.Any(pattern => GlobMatches(pattern, path));
        var excluded = !selector.Exclude.IsDefaultOrEmpty
            && selector.Exclude.Any(pattern => GlobMatches(pattern, path));
        return included && !excluded;
    }

    private static bool GlobMatches(string pattern, string path)
    {
        var patternSegments = pattern.Split('/');
        var pathSegments = path.Split('/');
        var next = new bool[pathSegments.Length + 1];
        next[^1] = true;
        for (var patternIndex = patternSegments.Length - 1;
            patternIndex >= 0;
            patternIndex--)
        {
            var current = new bool[pathSegments.Length + 1];
            if (patternSegments[patternIndex] == "**")
            {
                current[^1] = next[^1];
                for (var pathIndex = pathSegments.Length - 1;
                    pathIndex >= 0;
                    pathIndex--)
                {
                    current[pathIndex] = next[pathIndex]
                        || current[pathIndex + 1];
                }
            }
            else
            {
                for (var pathIndex = pathSegments.Length - 1;
                    pathIndex >= 0;
                    pathIndex--)
                {
                    current[pathIndex] = next[pathIndex + 1]
                        && SegmentMatches(
                            patternSegments[patternIndex],
                            pathSegments[pathIndex]);
                }
            }

            next = current;
        }

        return next[0];
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        var previous = new bool[segment.Length + 1];
        previous[0] = true;
        foreach (var character in pattern)
        {
            var current = new bool[segment.Length + 1];
            if (character == '*')
            {
                current[0] = previous[0];
                for (var index = 1; index <= segment.Length; index++)
                {
                    current[index] = current[index - 1] || previous[index];
                }
            }
            else
            {
                for (var index = 1; index <= segment.Length; index++)
                {
                    current[index] = previous[index - 1]
                        && character == segment[index - 1];
                }
            }

            previous = current;
        }

        return previous[segment.Length];
    }

    private static NormalizedContributionInput NormalizeInput(
        PolicyContributionInput input)
    {
        var project = NormalizePath(input.ProjectPath, "/projectPath");
        if (project.Failure is not null)
        {
            return NormalizedContributionInput.FromFailure(project.Failure);
        }

        return input switch
        {
            RepositoryPolicyContributionInput repository =>
                NormalizeRepository(project.Value!, repository.SourcePath),
            GeneratedPolicyContributionInput generated =>
                NormalizeGenerated(
                    project.Value!,
                    generated.ProducerKind,
                    generated.ProducerId,
                    generated.OutputId),
            UnvalidatedPolicyContributionInput raw =>
                NormalizeRaw(project.Value!, raw),
            _ => NormalizedContributionInput.FromFailure(new PolicyFailure(
                "policy.input.invalid-contribution",
                "/sourcePath")),
        };
    }

    private static NormalizedContributionInput NormalizeRaw(
        string projectPath,
        UnvalidatedPolicyContributionInput input)
    {
        var hasSource = input.SourcePath is not null;
        var hasGenerated = input.ProducerKind is not null
            || input.ProducerId is not null
            || input.OutputId is not null;
        if (!hasSource && !hasGenerated)
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "policy.input.invalid-contribution",
                "/sourcePath"));
        }

        if (hasSource && hasGenerated)
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "policy.input.invalid-contribution",
                "/generatedOutput"));
        }

        return hasSource
            ? NormalizeRepository(projectPath, input.SourcePath!)
            : NormalizeGenerated(
                projectPath,
                input.ProducerKind,
                input.ProducerId,
                input.OutputId);
    }

    private static NormalizedContributionInput NormalizeRepository(
        string projectPath,
        string sourcePath)
    {
        var source = NormalizePath(sourcePath, "/sourcePath");
        return source.Failure is not null
            ? NormalizedContributionInput.FromFailure(source.Failure)
            : new NormalizedContributionInput(projectPath, source.Value, null, null);
    }

    private static NormalizedContributionInput NormalizeGenerated(
        string projectPath,
        string? producerKind,
        string? producerId,
        string? outputId)
    {
        GeneratedOutputKind kind;
        string producerPrefix;
        string outputPrefix;
        switch (producerKind)
        {
            case "source-generator":
                kind = GeneratedOutputKind.SourceGenerator;
                producerPrefix = "sgp.";
                outputPrefix = "sgo.";
                break;
            case "tool-generated":
                kind = GeneratedOutputKind.ToolGenerated;
                producerPrefix = "tgp.";
                outputPrefix = "tgo.";
                break;
            default:
                return NormalizedContributionInput.FromFailure(new PolicyFailure(
                    "policy.input.invalid-contribution",
                    "/generatedOutput/producerKind"));
        }

        if (producerId is null)
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "run.generated.missing-identity",
                "/generatedOutput/producerId"));
        }

        if (!IsGeneratedId(producerId, producerPrefix))
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "run.generated.authority-conflict",
                "/generatedOutput/producerId"));
        }

        if (outputId is null)
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "run.generated.missing-identity",
                "/generatedOutput/outputId"));
        }

        if (!IsGeneratedId(outputId, outputPrefix))
        {
            return NormalizedContributionInput.FromFailure(new PolicyFailure(
                "run.generated.authority-conflict",
                "/generatedOutput/outputId"));
        }

        return new NormalizedContributionInput(
            projectPath,
            null,
            new GeneratedOutputIdentity(kind, producerId, outputId),
            null);
    }

    internal static (string? Value, PolicyFailure? Failure) NormalizePath(
        string path,
        string pointer)
    {
        if (string.IsNullOrEmpty(path)
            || path.Contains('\0')
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || IsDrivePath(path))
        {
            return (null, new PolicyFailure("policy.input.invalid-path", pointer));
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment == ".."))
        {
            return (null, new PolicyFailure("policy.input.invalid-path", pointer));
        }

        var normalized = segments
            .Where(segment => segment.Length > 0 && segment != ".")
            .ToArray();
        return normalized.Length == 0
            ? (null, new PolicyFailure("policy.input.invalid-path", pointer))
            : (string.Join('/', normalized), null);
    }

    private static string ContributionKey(PolicyContribution contribution) =>
        contribution switch
        {
            RepositoryPolicyContribution repository =>
                $"A\0{repository.ProjectPath}\0{repository.SourcePath}",
            GeneratedPolicyContribution generated =>
                $"B\0{generated.ProjectPath}\0{PolicyConfigurationVocabulary.GetId(generated.GeneratedOutput.ProducerKind)}\0{generated.GeneratedOutput.ProducerId}\0{generated.GeneratedOutput.OutputId}",
            _ => throw new InvalidOperationException("Unknown Policy contribution variant."),
        };

    private static PolicyFailure? ValidateSemantics(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("rules", out var rules))
        {
            return null;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenIds.Add(rule.GetProperty("id").GetString()!))
            {
                return new PolicyFailure(
                    "policy.semantic.duplicate-rule-id",
                    $"/rules/{index}/id");
            }

            index++;
        }

        var seenPriorities = new HashSet<int>();
        index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenPriorities.Add(rule.GetProperty("priority").GetInt32()))
            {
                return new PolicyFailure(
                    "policy.semantic.duplicate-priority",
                    $"/rules/{index}/priority");
            }

            index++;
        }

        index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var selectorName in new[] { "projectPaths", "sourcePaths" })
            {
                if (!rule.TryGetProperty(selectorName, out var selector))
                {
                    continue;
                }

                foreach (var memberName in new[] { "include", "exclude" })
                {
                    if (!selector.TryGetProperty(memberName, out var patterns))
                    {
                        continue;
                    }

                    var patternIndex = 0;
                    foreach (var pattern in patterns.EnumerateArray())
                    {
                        if (!IsValidPattern(pattern.GetString()!))
                        {
                            return new PolicyFailure(
                                "policy.semantic.invalid-pattern",
                                $"/rules/{index}/{selectorName}/{memberName}/{patternIndex}");
                        }

                        patternIndex++;
                    }
                }
            }

            index++;
        }

        return null;
    }

    private static List<SchemaFailure> CollectSchemaFailures(JsonElement root)
    {
        var failures = new List<SchemaFailure>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            failures.Add(new SchemaFailure(string.Empty, "type"));
            return failures;
        }

        var allowed = new HashSet<string>(
            ["schemaVersion", "targetProfile", "defaultDecision", "rules"],
            StringComparer.Ordinal);
        AddAdditionalPropertyFailures(root, string.Empty, allowed, failures);
        AddRequiredFailures(
            root,
            string.Empty,
            ["schemaVersion", "targetProfile", "defaultDecision"],
            failures);

        if (root.TryGetProperty("schemaVersion", out var version))
        {
            if (!IsJsonInteger(version))
            {
                failures.Add(new SchemaFailure("/schemaVersion", "type"));
            }

            if (!IsIntegerOne(version))
            {
                failures.Add(new SchemaFailure("/schemaVersion", "const"));
            }
        }

        if (root.TryGetProperty("targetProfile", out var profile))
        {
            ValidateEnumString(
                profile,
                "/targetProfile",
                ["profile.external-api", "profile.assembly-visible"],
                failures);
        }

        if (root.TryGetProperty("defaultDecision", out var defaultDecision))
        {
            ValidateDecision(defaultDecision, "/defaultDecision", failures);
        }

        if (root.TryGetProperty("rules", out var rules))
        {
            if (rules.ValueKind != JsonValueKind.Array)
            {
                failures.Add(new SchemaFailure("/rules", "type"));
            }
            else
            {
                var index = 0;
                foreach (var rule in rules.EnumerateArray())
                {
                    ValidateRule(rule, $"/rules/{index}", failures);
                    index++;
                }
            }
        }

        return failures;
    }

    private static void ValidateRule(
        JsonElement rule,
        string pointer,
        List<SchemaFailure> failures)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            failures.Add(new SchemaFailure(pointer, "type"));
            return;
        }

        var allowed = new HashSet<string>(
            ["id", "priority", "decision", "projectPaths", "sourcePaths"],
            StringComparer.Ordinal);
        AddAdditionalPropertyFailures(rule, pointer, allowed, failures);
        AddRequiredFailures(rule, pointer, ["id", "priority", "decision"], failures);

        if (rule.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String)
            {
                failures.Add(new SchemaFailure(pointer + "/id", "type"));
            }
            else if (!IsRuleId(id.GetString()!))
            {
                failures.Add(new SchemaFailure(pointer + "/id", "pattern"));
            }
        }

        if (rule.TryGetProperty("priority", out var priority))
        {
            if (!IsJsonInteger(priority))
            {
                failures.Add(new SchemaFailure(pointer + "/priority", "type"));
            }
            else if (TryGetDecimal(priority, out var numericPriority))
            {
                if (numericPriority < 0)
                {
                    failures.Add(new SchemaFailure(pointer + "/priority", "minimum"));
                }

                if (numericPriority > int.MaxValue)
                {
                    failures.Add(new SchemaFailure(pointer + "/priority", "maximum"));
                }
            }
        }

        if (rule.TryGetProperty("decision", out var decision))
        {
            ValidateDecision(decision, pointer + "/decision", failures);
        }

        foreach (var selectorName in new[] { "projectPaths", "sourcePaths" })
        {
            if (rule.TryGetProperty(selectorName, out var selector))
            {
                ValidateSelector(selector, pointer + "/" + selectorName, failures);
            }
        }
    }

    private static void ValidateSelector(
        JsonElement selector,
        string pointer,
        List<SchemaFailure> failures)
    {
        if (selector.ValueKind != JsonValueKind.Object)
        {
            failures.Add(new SchemaFailure(pointer, "type"));
            return;
        }

        var allowed = new HashSet<string>(["include", "exclude"], StringComparer.Ordinal);
        AddAdditionalPropertyFailures(selector, pointer, allowed, failures);
        if (!selector.EnumerateObject().Any())
        {
            failures.Add(new SchemaFailure(pointer, "minProperties"));
        }

        foreach (var memberName in new[] { "include", "exclude" })
        {
            if (!selector.TryGetProperty(memberName, out var patterns))
            {
                continue;
            }

            var memberPointer = pointer + "/" + memberName;
            if (patterns.ValueKind != JsonValueKind.Array)
            {
                failures.Add(new SchemaFailure(memberPointer, "type"));
                continue;
            }

            if (patterns.GetArrayLength() == 0)
            {
                failures.Add(new SchemaFailure(memberPointer, "minItems"));
            }

            var index = 0;
            foreach (var pattern in patterns.EnumerateArray())
            {
                var patternPointer = $"{memberPointer}/{index}";
                if (pattern.ValueKind != JsonValueKind.String)
                {
                    failures.Add(new SchemaFailure(patternPointer, "type"));
                }
                else if (pattern.GetString()!.Length == 0)
                {
                    failures.Add(new SchemaFailure(patternPointer, "minLength"));
                }

                index++;
            }
        }
    }

    private static void ValidateDecision(
        JsonElement value,
        string pointer,
        List<SchemaFailure> failures) =>
        ValidateEnumString(
            value,
            pointer,
            ["required", "optional", "forbidden"],
            failures);

    private static void ValidateEnumString(
        JsonElement value,
        string pointer,
        string[] allowed,
        List<SchemaFailure> failures)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            failures.Add(new SchemaFailure(pointer, "type"));
            failures.Add(new SchemaFailure(pointer, "enum"));
        }
        else if (!allowed.Contains(value.GetString()!, StringComparer.Ordinal))
        {
            failures.Add(new SchemaFailure(pointer, "enum"));
        }
    }

    private static void AddRequiredFailures(
        JsonElement value,
        string pointer,
        IEnumerable<string> required,
        List<SchemaFailure> failures)
    {
        foreach (var property in required)
        {
            if (!value.TryGetProperty(property, out _))
            {
                failures.Add(new SchemaFailure(pointer, "required"));
            }
        }
    }

    private static void AddAdditionalPropertyFailures(
        JsonElement value,
        string pointer,
        IReadOnlySet<string> allowed,
        List<SchemaFailure> failures)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                failures.Add(new SchemaFailure(
                    pointer + "/" + EscapePointerSegment(property.Name),
                    "additionalProperties"));
            }
        }
    }

    private static bool IsRuleId(string value)
    {
        if (value.Length is < 1 or > 64 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        return value.AsSpan(1).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-") < 0;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    private static bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)
            || pattern.StartsWith('/')
            || pattern.Contains('\\')
            || pattern.IndexOfAny(['?', '[', ']', '{', '}', '!']) >= 0)
        {
            return false;
        }

        var segments = pattern.Split('/', StringSplitOptions.None);
        return segments.All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && (segment == "**" || !segment.Contains("**", StringComparison.Ordinal)));
    }

    private static bool IsGeneratedId(string value, string prefix) =>
        value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsDrivePath(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static bool IsJsonInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && TryGetDecimal(value, out var number)
        && decimal.Truncate(number) == number;

    private static bool IsIntegerOne(JsonElement value) =>
        IsJsonInteger(value)
        && TryGetDecimal(value, out var number)
        && number == 1;

    private static bool TryGetDecimal(JsonElement value, out decimal number)
    {
        number = default;
        return value.ValueKind == JsonValueKind.Number
            && decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
    }

    private static bool IsIntegerOtherThanOne(string number)
    {
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? number[..exponentIndex] : number;
        var exponentText = exponentIndex >= 0 ? number[(exponentIndex + 1)..] : "0";
        var decimalIndex = mantissa.IndexOf('.');
        var fractionDigits = decimalIndex >= 0 ? mantissa.Length - decimalIndex - 1 : 0;
        var digits = mantissa
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        var isZero = digits.All(character => character == '0');
        if (isZero)
        {
            return true;
        }

        if (!long.TryParse(
            exponentText,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var exponent))
        {
            return !exponentText.StartsWith("-", StringComparison.Ordinal);
        }

        int requiredTrailingZeros;
        if (exponent < 0)
        {
            if (exponent == long.MinValue || -exponent >= digits.Length)
            {
                return false;
            }

            requiredTrailingZeros = (int)-exponent + fractionDigits;
        }
        else if (exponent > fractionDigits)
        {
            return true;
        }
        else
        {
            requiredTrailingZeros = fractionDigits - (int)exponent;
        }

        var trailingZeros = digits.Reverse()
            .TakeWhile(character => character == '0')
            .Count();
        if (trailingZeros < requiredTrailingZeros)
        {
            return false;
        }

        if (number.StartsWith("-", StringComparison.Ordinal))
        {
            return true;
        }

        var retainedDigitCount = digits.Length - requiredTrailingZeros;
        return digits[..retainedDigitCount].TrimStart('0') != "1";
    }

    private static string? FindDuplicatePropertyPointer(byte[] payload)
    {
        var containers = new Stack<JsonContainer>();
        string? pendingPointer = null;
        var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                containers.Push(new JsonContainer(
                    ConsumeContainerPointer(containers, ref pendingPointer),
                    isObject: true));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                containers.Pop();
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                containers.Push(new JsonContainer(
                    ConsumeContainerPointer(containers, ref pendingPointer),
                    isObject: false));
            }
            else if (reader.TokenType == JsonTokenType.EndArray)
            {
                containers.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString()!;
                var current = containers.Peek();
                if (!current.PropertyNames!.Add(propertyName))
                {
                    return current.Pointer + "/" + EscapePointerSegment(propertyName);
                }

                pendingPointer = current.Pointer + "/" + EscapePointerSegment(propertyName);
            }
            else if (reader.TokenType is not JsonTokenType.Comment)
            {
                if (containers.TryPeek(out var current) && !current.IsObject)
                {
                    current.NextArrayIndex++;
                }

                pendingPointer = null;
            }
        }

        return null;
    }

    private static string ConsumeContainerPointer(
        Stack<JsonContainer> containers,
        ref string? pendingPointer)
    {
        if (pendingPointer is not null)
        {
            var pointer = pendingPointer;
            pendingPointer = null;
            return pointer;
        }

        if (containers.TryPeek(out var parent) && !parent.IsObject)
        {
            return parent.Pointer + "/" + parent.NextArrayIndex++;
        }

        return string.Empty;
    }

    private static string EscapePointerSegment(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static bool HasUtf8Bom(byte[] payload) =>
        payload.Length >= 3
        && payload[0] == 0xef
        && payload[1] == 0xbb
        && payload[2] == 0xbf;

    private static PolicyParseOutcome Failure(
        string code,
        string? pointer = null,
        string? schemaKeyword = null) =>
        PolicyParseOutcome.Failure(new PolicyFailure(code, pointer, schemaKeyword));

    private static PolicyEvaluationOutcome EvaluationFailure(
        string code,
        string? pointer = null) =>
        PolicyEvaluationOutcome.Failure(new PolicyFailure(code, pointer));

    private sealed class JsonContainer(string pointer, bool isObject)
    {
        public string Pointer { get; } = pointer;

        public bool IsObject { get; } = isObject;

        public HashSet<string>? PropertyNames { get; } = isObject
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;

        public int NextArrayIndex { get; set; }
    }

    private readonly record struct SchemaFailure(string Pointer, string Keyword);

    private sealed record NormalizedContributionInput(
        string ProjectPath,
        string? SourcePath,
        GeneratedOutputIdentity? GeneratedOutput,
        PolicyFailure? Failure)
    {
        internal static NormalizedContributionInput FromFailure(
            PolicyFailure failure) =>
            new(string.Empty, null, null, failure);
    }
}
