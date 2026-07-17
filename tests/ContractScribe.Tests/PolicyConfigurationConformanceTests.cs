using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class PolicyConfigurationConformanceTests
{
    private static readonly Lazy<JsonSchema> PolicySchema = new(() => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", "policy-configuration", "v1.schema.json"))));

    [Fact]
    public void ConformanceCases_ProduceTheDocumentedOutcomesDeterministically()
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "policy-configuration", "v1");
        var schema = PolicySchema.Value;
        var manifest = JsonSerializer.Deserialize<ConformanceManifest>(
            File.ReadAllText(Path.Combine(fixtureRoot, "cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
            ?? throw new InvalidOperationException("The conformance fixture manifest must deserialize.");
        PolicyConfigurationV1Conformance.ValidateManifest(fixtureRoot, manifest);

        foreach (var conformanceCase in manifest.Cases)
        {
            var first = PolicyConfigurationV1Conformance.Evaluate(fixtureRoot, schema, conformanceCase);
            var second = PolicyConfigurationV1Conformance.Evaluate(fixtureRoot, schema, conformanceCase);

            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            Assert.Equal(conformanceCase.Expected.Decision, first.Decision);
            Assert.Equal(conformanceCase.Expected.MatchedRuleId, first.MatchedRuleId);
            Assert.Equal(conformanceCase.Expected.Error?.Code, first.Error?.Code);
            Assert.Equal(conformanceCase.Expected.Error?.Pointer, first.Error?.Pointer);
            Assert.Equal(conformanceCase.Expected.Error?.SchemaKeyword, first.Error?.SchemaKeyword);
            Assert.Equal(conformanceCase.Stage, PolicyConfigurationV1Conformance.GetStage(first));
        }
    }

    [Fact]
    public void RawByteFailures_PrecedeLexicalParsing()
    {
        var schema = JsonSchema.FromText("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");
        var input = new EvaluationInput("projects/App/App.csproj", "src/App/File.cs");

        Assert.Equal(
            "policy.document.bom-not-allowed",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{}")).ToArray(), schema, input).Error?.Code);
        Assert.Equal(
            "policy.document.invalid-encoding",
            PolicyConfigurationV1Conformance.EvaluateBytes([0xff], schema, input).Error?.Code);
    }

    [Fact]
    public void SchemaVersionGate_AppliesOnlyToOneIntegerVersionOnAnObject()
    {
        var schema = PolicySchema.Value;
        var input = new EvaluationInput("projects/App/App.csproj", "src/App/File.cs");

        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":2e0,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":2147483648,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.document.duplicate-property",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"schemaVersion\":1,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "optional",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":1.0,\"defaultDecision\":\"optional\"}"), schema, input).Decision);
        Assert.Equal(
            "policy.schema.invalid-document",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":\"2\",\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.invalid-document",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("[]"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":-1,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":-1.0,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.unsupported-version",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":-10e-1,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
        Assert.Equal(
            "policy.schema.invalid-document",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":-1.5,\"defaultDecision\":\"optional\"}"), schema, input).Error?.Code);
    }

    [Fact]
    public void NestedDuplicateProperties_UseTheCurrentMemberPointerAndShortCircuitLaterSyntax()
    {
        var schema = PolicySchema.Value;
        var input = new EvaluationInput("projects/App/App.csproj", "src/App/File.cs");
        var nestedDuplicate = "{\"schemaVersion\":1,\"defaultDecision\":\"optional\",\"rules\":[{\"id\":\"first\",\"id\":\"second\",\"priority\":1,\"decision\":\"required\"}]}";
        var duplicateBeforeSyntaxError = "{\"schemaVersion\":1,\"schemaVersion\":1,\"defaultDecision\":\"optional\",";

        Assert.Equal(
            "/rules/0/id",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes(nestedDuplicate), schema, input).Error?.Pointer);
        Assert.Equal(
            "policy.document.duplicate-property",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes(duplicateBeforeSyntaxError), schema, input).Error?.Code);
        Assert.Equal(
            "/rules/1/x",
            PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"defaultDecision\":\"optional\",\"rules\":[0,{\"x\":1,\"x\":2}]}"), schema, input).Error?.Pointer);
    }

    [Fact]
    public void GlobsSelectorsAndSemanticPriority_HaveNormativeOutcomes()
    {
        var schema = PolicySchema.Value;
        var input = new EvaluationInput("projects/App/App.csproj", "a/b");
        var zeroSegmentGlob = "{\"schemaVersion\":1,\"defaultDecision\":\"optional\",\"rules\":[{\"id\":\"glob\",\"priority\":0,\"decision\":\"required\",\"sourcePaths\":{\"include\":[\"a/**/b\"]}}]}";
        var excludeWins = "{\"schemaVersion\":1,\"defaultDecision\":\"optional\",\"rules\":[{\"id\":\"excluded\",\"priority\":0,\"decision\":\"required\",\"sourcePaths\":{\"include\":[\"**\"],\"exclude\":[\"a/**\"]}}]}";
        var semanticPriority = "{\"schemaVersion\":1,\"defaultDecision\":\"optional\",\"rules\":[{\"id\":\"same\",\"priority\":0,\"decision\":\"required\",\"sourcePaths\":{\"include\":[\"a**b\"]}},{\"id\":\"same\",\"priority\":0,\"decision\":\"forbidden\"}]}";

        Assert.Equal("required", PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes(zeroSegmentGlob), schema, input).Decision);
        Assert.Equal("optional", PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes(excludeWins), schema, input).Decision);
        Assert.Equal("policy.semantic.duplicate-rule-id", PolicyConfigurationV1Conformance.EvaluateBytes(Encoding.UTF8.GetBytes(semanticPriority), schema, input).Error?.Code);
    }

    [Fact]
    public void ManifestIntegrity_RejectsDuplicateIdsAndAmbiguousPayloads()
    {
        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "policy-configuration", "v1");
        var input = new EvaluationInput("projects/App/App.csproj", "src/App/File.cs");
        var missingDocument = new ExpectedOutcome(null, null, new ExpectedError("policy.input.missing-document", null, null));
        var duplicateCase = new ConformanceCase("duplicate", "document", null, null, null, input, missingDocument);

        Assert.Throws<InvalidOperationException>(() => PolicyConfigurationV1Conformance.ValidateManifest(fixtureRoot, new ConformanceManifest([duplicateCase, duplicateCase])));
        Assert.Throws<InvalidOperationException>(() => PolicyConfigurationV1Conformance.ValidateManifest(fixtureRoot, new ConformanceManifest([
            new ConformanceCase("ambiguous", "raw-bytes", "policies/minimal-valid.json", "policies/invalid-utf8.base64", "base64", input, new ExpectedOutcome(null, null, new ExpectedError("policy.document.invalid-encoding", null, null)))])));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root from the test base directory.");
    }
}

internal sealed record ConformanceManifest([property: JsonPropertyName("cases")] List<ConformanceCase> Cases);

internal sealed record ConformanceCase(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("policyFile")] string? PolicyFile,
    [property: JsonPropertyName("payloadFile")] string? PayloadFile,
    [property: JsonPropertyName("payloadEncoding")] string? PayloadEncoding,
    [property: JsonPropertyName("input")] EvaluationInput? Input,
    [property: JsonPropertyName("expected")] ExpectedOutcome Expected);

internal sealed record EvaluationInput(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("sourcePath")] string SourcePath);

internal sealed record ExpectedOutcome(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("matchedRuleId")] string? MatchedRuleId,
    [property: JsonPropertyName("error")] ExpectedError? Error);

internal sealed record ExpectedError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("pointer")] string? Pointer,
    [property: JsonPropertyName("schemaKeyword")] string? SchemaKeyword);

internal sealed record ConformanceOutcome(string? Decision = null, string? MatchedRuleId = null, ConformanceError? Error = null);

internal sealed record ConformanceError(string Code, string? Pointer = null, string? SchemaKeyword = null);

internal static class PolicyConfigurationV1Conformance
{
    public static ConformanceOutcome Evaluate(string fixtureRoot, JsonSchema schema, ConformanceCase conformanceCase)
    {
        if (conformanceCase.PayloadFile is not null)
        {
            var payloadPath = GetFixturePayloadPath(fixtureRoot, conformanceCase.PayloadFile);
            var payload = conformanceCase.PayloadEncoding == "base64"
                ? Convert.FromBase64String(File.ReadAllText(payloadPath).Trim())
                : File.ReadAllBytes(payloadPath);
            return EvaluateBytes(payload, schema, conformanceCase.Input ?? throw new InvalidOperationException("A raw payload case needs input."));
        }

        if (conformanceCase.PolicyFile is null)
        {
            return Error("policy.input.missing-document");
        }

        var policyPath = GetFixturePayloadPath(fixtureRoot, conformanceCase.PolicyFile);
        return EvaluateBytes(File.ReadAllBytes(policyPath), schema, conformanceCase.Input ?? throw new InvalidOperationException("A policy case needs input."));
    }

    public static void ValidateManifest(string fixtureRoot, ConformanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conformanceCase in manifest.Cases)
        {
            if (string.IsNullOrWhiteSpace(conformanceCase.CaseId) || !caseIds.Add(conformanceCase.CaseId))
            {
                throw new InvalidOperationException("Every conformance case must have a unique non-empty caseId.");
            }

            if (conformanceCase.Input is null || conformanceCase.Expected is null)
            {
                throw new InvalidOperationException($"Conformance case '{conformanceCase.CaseId}' must declare input and expected outcome.");
            }

            var hasPolicyFile = conformanceCase.PolicyFile is not null;
            var hasPayloadFile = conformanceCase.PayloadFile is not null;
            if (hasPolicyFile == hasPayloadFile)
            {
                if (conformanceCase.Stage != "document" || hasPolicyFile)
                {
                    throw new InvalidOperationException($"Conformance case '{conformanceCase.CaseId}' must declare exactly one fixture payload, except missing-document.");
                }
            }

            if (hasPolicyFile)
            {
                _ = GetFixturePayloadPath(fixtureRoot, conformanceCase.PolicyFile!);
                if (conformanceCase.PayloadEncoding is not null)
                {
                    throw new InvalidOperationException($"Policy case '{conformanceCase.CaseId}' cannot declare payloadEncoding.");
                }
            }

            if (hasPayloadFile)
            {
                _ = GetFixturePayloadPath(fixtureRoot, conformanceCase.PayloadFile!);
                if (conformanceCase.PayloadEncoding is not null and not "base64")
                {
                    throw new InvalidOperationException($"Raw case '{conformanceCase.CaseId}' has unsupported payloadEncoding.");
                }
            }

            var isSuccess = conformanceCase.Expected.Decision is not null;
            var isError = conformanceCase.Expected.Error is not null;
            if (isSuccess == isError || (!isSuccess && conformanceCase.Expected.MatchedRuleId is not null))
            {
                throw new InvalidOperationException($"Conformance case '{conformanceCase.CaseId}' must declare exactly one valid outcome shape.");
            }

            if (isSuccess && conformanceCase.Expected.Decision is not ("required" or "optional" or "forbidden"))
            {
                throw new InvalidOperationException($"Conformance case '{conformanceCase.CaseId}' has an unknown decision.");
            }

            var expectedStage = isSuccess ? "resolution" : GetStage(conformanceCase.Expected.Error!.Code);
            if (conformanceCase.Stage != expectedStage)
            {
                throw new InvalidOperationException($"Conformance case '{conformanceCase.CaseId}' declares stage '{conformanceCase.Stage}' but its expected outcome belongs to '{expectedStage}'.");
            }

            if (conformanceCase.Expected.Error?.SchemaKeyword is not null && conformanceCase.Stage != "schema")
            {
                throw new InvalidOperationException($"Only schema-stage errors may declare schemaKeyword.");
            }
        }
    }

    public static string GetStage(ConformanceOutcome outcome)
    {
        return outcome.Error is null ? "resolution" : GetStage(outcome.Error.Code);
    }

    private static string GetStage(string errorCode)
    {
        return errorCode switch
        {
            "policy.input.missing-document" => "document",
            "policy.document.invalid-encoding" or "policy.document.bom-not-allowed" => "raw-bytes",
            "policy.document.invalid-json" or "policy.document.duplicate-property" => "json",
            "policy.schema.unsupported-version" => "version",
            "policy.schema.invalid-document" => "schema",
            "policy.semantic.duplicate-rule-id" or "policy.semantic.duplicate-priority" or "policy.semantic.invalid-pattern" => "semantic",
            "policy.input.invalid-path" => "input",
            _ => throw new InvalidOperationException($"Unknown conformance error code '{errorCode}'.")
        };
    }

    private static string GetFixturePayloadPath(string fixtureRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.StartsWith("policies/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture payloads must be relative files under policies/.");
        }

        var root = Path.GetFullPath(Path.Combine(fixtureRoot, "policies")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw new InvalidOperationException("Fixture payload file must exist beneath the fixture root.");
        }

        return path;
    }

    public static ConformanceOutcome EvaluateBytes(byte[] payload, JsonSchema schema, EvaluationInput input)
    {
        if (HasUtf8Bom(payload))
        {
            return Error("policy.document.bom-not-allowed");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return Error("policy.document.invalid-encoding");
        }

        JsonDocument document;
        try
        {
            var duplicatePointer = FindDuplicatePropertyPointer(payload);
            if (duplicatePointer is not null)
            {
                return Error("policy.document.duplicate-property", duplicatePointer);
            }

            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            return Error("policy.document.invalid-json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number && IsIntegerOtherThanOne(version.GetRawText()))
            {
                return Error("policy.schema.unsupported-version", "/schemaVersion");
            }

            var evaluation = schema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var schemaFailure = FindSchemaFailure(evaluation);
                return Error("policy.schema.invalid-document", schemaFailure.Pointer, schemaFailure.Keyword);
            }

            var semanticError = ValidateSemantics(root);
            if (semanticError is not null)
            {
                return new ConformanceOutcome(Error: semanticError);
            }

            var normalizedProject = NormalizeInputPath(input.ProjectPath, "/projectPath");
            if (normalizedProject.Error is not null)
            {
                return new ConformanceOutcome(Error: normalizedProject.Error);
            }

            var normalizedSource = NormalizeInputPath(input.SourcePath, "/sourcePath");
            if (normalizedSource.Error is not null)
            {
                return new ConformanceOutcome(Error: normalizedSource.Error);
            }

            return Resolve(root, normalizedProject.Value!, normalizedSource.Value!);
        }
    }

    private static ConformanceOutcome Resolve(JsonElement root, string projectPath, string sourcePath)
    {
        JsonElement? selectedRule = null;
        var selectedPriority = -1;
        if (root.TryGetProperty("rules", out var rules))
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (RuleApplies(rule, projectPath, sourcePath) && rule.GetProperty("priority").GetInt32() > selectedPriority)
                {
                    selectedRule = rule;
                    selectedPriority = rule.GetProperty("priority").GetInt32();
                }
            }
        }

        return selectedRule is { } selected
            ? new ConformanceOutcome(selected.GetProperty("decision").GetString(), selected.GetProperty("id").GetString())
            : new ConformanceOutcome(root.GetProperty("defaultDecision").GetString());
    }

    private static bool RuleApplies(JsonElement rule, string projectPath, string sourcePath)
    {
        return (!rule.TryGetProperty("projectPaths", out var projectSelector) || SelectorAccepts(projectSelector, projectPath))
            && (!rule.TryGetProperty("sourcePaths", out var sourceSelector) || SelectorAccepts(sourceSelector, sourcePath));
    }

    private static bool SelectorAccepts(JsonElement selector, string path)
    {
        var included = !selector.TryGetProperty("include", out var includes)
            || includes.EnumerateArray().Any(pattern => GlobMatches(pattern.GetString()!, path));
        var excluded = selector.TryGetProperty("exclude", out var excludes)
            && excludes.EnumerateArray().Any(pattern => GlobMatches(pattern.GetString()!, path));
        return included && !excluded;
    }

    private static ConformanceError? ValidateSemantics(JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rules))
        {
            return null;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!seenIds.Add(rule.GetProperty("id").GetString()!))
            {
                return new ConformanceError("policy.semantic.duplicate-rule-id", $"/rules/{index}/id");
            }

            index++;
        }

        var seenPriorities = new HashSet<int>();
        index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!seenPriorities.Add(rule.GetProperty("priority").GetInt32()))
            {
                return new ConformanceError("policy.semantic.duplicate-priority", $"/rules/{index}/priority");
            }

            index++;
        }

        index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
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
                            return new ConformanceError("policy.semantic.invalid-pattern", $"/rules/{index}/{selectorName}/{memberName}/{patternIndex}");
                        }

                        patternIndex++;
                    }
                }
            }

            index++;
        }

        return null;
    }

    private static (string? Value, ConformanceError? Error) NormalizeInputPath(string path, string pointer)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.StartsWith('/') || path.StartsWith('\\') || IsDrivePath(path))
        {
            return (null, new ConformanceError("policy.input.invalid-path", pointer));
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment == ".."))
        {
            return (null, new ConformanceError("policy.input.invalid-path", pointer));
        }

        var normalized = segments.Where(segment => segment.Length > 0 && segment != ".").ToArray();
        return normalized.Length == 0
            ? (null, new ConformanceError("policy.input.invalid-path", pointer))
            : (string.Join('/', normalized), null);
    }

    private static bool IsDrivePath(string path)
    {
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }

    private static bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.StartsWith('/') || pattern.IndexOf('\\') >= 0 || pattern.IndexOfAny(['?', '[', ']', '{', '}', '!']) >= 0)
        {
            return false;
        }

        var segments = pattern.Split('/', StringSplitOptions.None);
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." && (segment == "**" || !segment.Contains("**", StringComparison.Ordinal)));
    }

    private static bool GlobMatches(string pattern, string path)
    {
        return GlobMatches(pattern.Split('/'), 0, path.Split('/'), 0);
    }

    private static bool GlobMatches(string[] pattern, int patternIndex, string[] path, int pathIndex)
    {
        if (patternIndex == pattern.Length)
        {
            return pathIndex == path.Length;
        }

        if (pattern[patternIndex] == "**")
        {
            return Enumerable.Range(pathIndex, path.Length - pathIndex + 1)
                .Any(nextPathIndex => GlobMatches(pattern, patternIndex + 1, path, nextPathIndex));
        }

        return pathIndex < path.Length
            && SegmentMatches(pattern[patternIndex], path[pathIndex])
            && GlobMatches(pattern, patternIndex + 1, path, pathIndex + 1);
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
                    current[index] = previous[index - 1] && character == segment[index - 1];
                }
            }

            previous = current;
        }

        return previous[segment.Length];
    }

    private static bool IsIntegerOtherThanOne(string number)
    {
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? number[..exponentIndex] : number;
        var exponentText = exponentIndex >= 0 ? number[(exponentIndex + 1)..] : "0";
        var decimalIndex = mantissa.IndexOf('.');
        var fractionDigits = decimalIndex >= 0 ? mantissa.Length - decimalIndex - 1 : 0;
        var digits = mantissa.Replace("-", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal);
        var isZero = digits.All(character => character == '0');

        if (isZero)
        {
            return true;
        }

        if (!long.TryParse(exponentText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var exponent))
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

        var trailingZeros = digits.Reverse().TakeWhile(character => character == '0').Count();
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

    private static (string Pointer, string Keyword) FindSchemaFailure(EvaluationResults evaluation)
    {
        var failures = new List<(string Pointer, string Keyword)>();
        CollectSchemaFailures(evaluation, failures);
        return failures
            .OrderBy(failure => failure.Pointer, StringComparer.Ordinal)
            .ThenBy(failure => failure.Keyword, StringComparer.Ordinal)
            .FirstOrDefault(("", "unknown"));
    }

    private static void CollectSchemaFailures(EvaluationResults result, List<(string Pointer, string Keyword)> failures)
    {
        if (result.Errors is not null)
        {
            foreach (var failure in result.Errors)
            {
                var keyword = string.IsNullOrEmpty(failure.Key)
                    ? result.EvaluationPath.ToString().Split('/').Last()
                    : failure.Key;
                failures.Add((result.InstanceLocation.ToString(), keyword));
            }
        }

        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
            {
                CollectSchemaFailures(detail, failures);
            }
        }
    }

    private static string? FindDuplicatePropertyPointer(byte[] payload)
    {
        var seen = new Stack<JsonContainer>();
        string? pendingPointer = null;
        var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                seen.Push(new JsonContainer(ConsumeContainerPointer(seen, ref pendingPointer), isObject: true));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                seen.Pop();
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                seen.Push(new JsonContainer(ConsumeContainerPointer(seen, ref pendingPointer), isObject: false));
            }
            else if (reader.TokenType == JsonTokenType.EndArray)
            {
                seen.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString()!;
                var current = seen.Peek();
                if (!current.PropertyNames!.Add(propertyName))
                {
                    return current.Pointer + "/" + EscapePointerSegment(propertyName);
                }

                pendingPointer = current.Pointer + "/" + EscapePointerSegment(propertyName);
            }
            else if (reader.TokenType is not JsonTokenType.Comment)
            {
                if (seen.TryPeek(out var current) && !current.IsObject)
                {
                    current.NextArrayIndex++;
                }

                pendingPointer = null;
            }
        }

        return null;
    }

    private static string ConsumeContainerPointer(Stack<JsonContainer> stack, ref string? pendingPointer)
    {
        if (pendingPointer is not null)
        {
            var pointer = pendingPointer;
            pendingPointer = null;
            return pointer;
        }

        if (stack.TryPeek(out var parent) && !parent.IsObject)
        {
            return parent.Pointer + "/" + parent.NextArrayIndex++;
        }

        return string.Empty;
    }

    private static string EscapePointerSegment(string segment)
    {
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static bool HasUtf8Bom(byte[] payload)
    {
        return payload.Length >= 3 && payload[0] == 0xef && payload[1] == 0xbb && payload[2] == 0xbf;
    }

    private static ConformanceOutcome Error(string code, string? pointer = null)
    {
        return new ConformanceOutcome(Error: new ConformanceError(code, pointer));
    }

    private static ConformanceOutcome Error(string code, string? pointer, string? schemaKeyword)
    {
        return new ConformanceOutcome(Error: new ConformanceError(code, pointer, schemaKeyword));
    }

    private sealed class JsonContainer(string pointer, bool isObject)
    {
        public string Pointer { get; } = pointer;
        public bool IsObject { get; } = isObject;
        public HashSet<string>? PropertyNames { get; } = isObject ? new HashSet<string>(StringComparer.Ordinal) : null;
        public int NextArrayIndex { get; set; }
    }
}
