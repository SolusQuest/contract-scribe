using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContractScribe.Evaluation;

internal sealed record EvaluationManifest
{
    public required int SchemaVersion { get; init; }

    public required string CorpusId { get; init; }

    public required string RepositoryProject { get; init; }

    public required string SelectionFile { get; init; }

    public required string SafetyGateCaseId { get; init; }

    public required EvaluationManifestFile[] Files { get; init; }

    public required EvaluationScenario[] Scenarios { get; init; }
}

internal sealed record EvaluationManifestFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }
}

internal sealed record EvaluationScenario
{
    public required string Id { get; init; }

    public required string TargetDocumentationId { get; init; }

    public required string Script { get; init; }

    public required string ExecutionDomain { get; init; }

    public required string ExpectedStatus { get; init; }

    public required string ExpectedCode { get; init; }

    public required string[] Coverage { get; init; }

    public required EvaluationOfflineExpectation OfflineExpectation { get; init; }

    public string? ProposalLine { get; init; }

    public bool AddEvidenceConflict { get; init; }

    public string? RequiredPlatform { get; init; }

    public string? NonRequiredPlatformStatus { get; init; }

    public string? NonRequiredPlatformCode { get; init; }

    public int? MaximumElapsedMillisecondsOverride { get; init; }
}

internal sealed record EvaluationOfflineExpectation
{
    public required int AttemptCount { get; init; }

    public required int ProviderRequestCount { get; init; }

    public required int ToolRoundCount { get; init; }

    public required int ToolCallCount { get; init; }

    public required string ProposalStatus { get; init; }

    public EvaluationUsageExpectation? Usage { get; init; }

    public required string[] ObservationIds { get; init; }
}

internal sealed record EvaluationUsageExpectation
{
    public required int? InputTokens { get; init; }

    public required int? OutputTokens { get; init; }

    public required int? CachedInputTokens { get; init; }

    public required int? UncachedInputTokens { get; init; }

    public required int? ReasoningTokens { get; init; }

    public required string? CacheObservation { get; init; }
}

internal sealed record EvaluationSelection
{
    public required int SchemaVersion { get; init; }

    public required string SelectionId { get; init; }

    public required string Endpoint { get; init; }

    public required string Model { get; init; }

    public required string EvidenceDate { get; init; }

    public required string[] Documentation { get; init; }

    public required string Reason { get; init; }

    public required string[] ExpectedObservations { get; init; }

    public required string[] LiveScenarioIds { get; init; }

    public required EvaluationSelectionLimits Limits { get; init; }
}

internal sealed record EvaluationSelectionLimits
{
    public required int MaximumAttempts { get; init; }

    public required int MaximumContextReferences { get; init; }

    public required int MaximumContextUtf8Bytes { get; init; }

    public required int MaximumEvidenceReferences { get; init; }

    public required int MaximumEvidenceUtf8Bytes { get; init; }

    public required int MaximumProviderRequests { get; init; }

    public required int MaximumToolRounds { get; init; }

    public required int MaximumToolCalls { get; init; }

    public required int MaximumInputTokens { get; init; }

    public required int MaximumUncachedInputTokens { get; init; }

    public required int MaximumOutputTokens { get; init; }

    public required long MaximumCostMicrounits { get; init; }

    public required int MaximumElapsedMilliseconds { get; init; }
}

internal sealed record LoadedEvaluationManifest(
    string CorpusDirectory,
    string CorpusIdentity,
    string SelectionIdentity,
    EvaluationManifest Manifest,
    EvaluationSelection Selection);

internal static class EvaluationManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static LoadedEvaluationManifest Load(string corpusDirectory)
    {
        var root = Path.GetFullPath(corpusDirectory);
        if (!Directory.Exists(root)
            || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("evaluation.manifest.path-invalid");
        }

        var manifestPath = Path.Join(root, "manifest.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<EvaluationManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("evaluation.manifest.invalid");
        ValidateManifest(manifest);
        ValidateFileClosure(root, manifest);
        var identities = new List<string>();
        foreach (var entry in manifest.Files.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            var path = ResolveFile(root, entry.Path);
            var digest = Sha256(File.ReadAllBytes(path));
            if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("evaluation.manifest.file-mismatch");
            }

            identities.Add(entry.Path + "\0" + digest);
        }

        var selectionPath = ResolveFile(root, manifest.SelectionFile);
        var selectionBytes = File.ReadAllBytes(selectionPath);
        if (Sha256(selectionBytes) != manifest.Files.Single(file =>
                file.Path == manifest.SelectionFile).Sha256)
        {
            throw new InvalidDataException("evaluation.manifest.file-mismatch");
        }

        var selection = JsonSerializer.Deserialize<EvaluationSelection>(
            selectionBytes,
            JsonOptions) ?? throw new InvalidDataException("evaluation.selection.invalid");
        ValidateSelection(selection);
        if (!selection.LiveScenarioIds.All(id => manifest.Scenarios.Any(scenario =>
                scenario.Id == id && scenario.ExecutionDomain == "live-and-offline"))
            || !selection.LiveScenarioIds.Contains(manifest.SafetyGateCaseId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("evaluation.selection.invalid");
        }

        var identityBytes = Encoding.UTF8.GetBytes(
            Sha256(manifestBytes) + "\n" + string.Join("\n", identities));
        return new LoadedEvaluationManifest(
            root,
            Sha256(identityBytes),
            Sha256(selectionBytes),
            manifest,
            selection);
    }

    private static void ValidateManifest(EvaluationManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || !IsId(manifest.CorpusId)
            || !IsRelative(manifest.RepositoryProject)
            || !IsRelative(manifest.SelectionFile)
            || manifest.Files.Length == 0
            || manifest.Scenarios.Length == 0
            || manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count()
                != manifest.Files.Length
            || !manifest.Files.Select(file => file.Path).SequenceEqual(
                manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            || manifest.Scenarios.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count()
                != manifest.Scenarios.Length
            || !manifest.Scenarios.Any(scenario => scenario.Id == manifest.SafetyGateCaseId))
        {
            throw new InvalidDataException("evaluation.manifest.invalid");
        }

        if (!manifest.Files.Any(file => file.Path == manifest.RepositoryProject)
            || !manifest.Files.Any(file => file.Path == manifest.SelectionFile))
        {
            throw new InvalidDataException("evaluation.manifest.invalid");
        }

        foreach (var file in manifest.Files)
        {
            if (!IsRelative(file.Path) || !IsSha256(file.Sha256))
            {
                throw new InvalidDataException("evaluation.manifest.invalid");
            }
        }

        foreach (var scenario in manifest.Scenarios)
        {
            if (!IsId(scenario.Id)
                || string.IsNullOrEmpty(scenario.TargetDocumentationId)
                || !KnownScript(scenario.Script)
                || scenario.ExecutionDomain is not ("offline-only" or "live-and-offline")
                || !IsId(scenario.ExpectedStatus)
                || !IsId(scenario.ExpectedCode)
                || scenario.Coverage.Length == 0
                || scenario.Coverage.Any(coverage => !IsId(coverage))
                || !scenario.Coverage.SequenceEqual(
                    scenario.Coverage.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal)
                || !ValidOfflineExpectation(scenario)
                || scenario.ProposalLine is { } line
                    && (line.Length == 0 || line.Length > 400 || line.Any(char.IsControl))
                || scenario.RequiredPlatform is not (null or "linux" or "windows" or "macos")
                || (scenario.RequiredPlatform is null)
                    != (scenario.NonRequiredPlatformStatus is null)
                || (scenario.RequiredPlatform is null)
                    != (scenario.NonRequiredPlatformCode is null)
                || scenario.NonRequiredPlatformStatus is { } platformStatus
                    && !IsId(platformStatus)
                || scenario.NonRequiredPlatformCode is { } platformCode
                    && !IsId(platformCode)
                || scenario.MaximumElapsedMillisecondsOverride is { } maximumElapsed
                    && maximumElapsed is < 1 or > 120_000)
            {
                throw new InvalidDataException("evaluation.manifest.invalid");
            }
        }
    }

    private static bool ValidOfflineExpectation(EvaluationScenario scenario)
    {
        if (scenario.OfflineExpectation is not { } expected)
        {
            return false;
        }

        if (expected.AttemptCount is < 0 or > 2
            || expected.ProviderRequestCount is < 0 or > 8
            || expected.ToolRoundCount is < 0 or > 4
            || expected.ToolCallCount is < 0 or > 16
            || expected.ProposalStatus is not ("validated" or "not-reported")
            || (scenario.ProposalLine is null) != (expected.ProposalStatus == "not-reported")
            || expected.ObservationIds.Any(observation => !IsId(observation))
            || !expected.ObservationIds.SequenceEqual(
                expected.ObservationIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return false;
        }

        if (expected.Usage is not { } usage)
        {
            return !expected.ObservationIds.Contains("usage-observed", StringComparer.Ordinal)
                && !expected.ObservationIds.Any(observation => observation.StartsWith(
                    "cache.",
                    StringComparison.Ordinal));
        }

        return new[]
            {
                usage.InputTokens,
                usage.OutputTokens,
                usage.CachedInputTokens,
                usage.UncachedInputTokens,
                usage.ReasoningTokens,
            }.Any(value => value is not null)
            && new[]
            {
                usage.InputTokens,
                usage.CachedInputTokens,
                usage.UncachedInputTokens,
            }.All(value => value is null or >= 0 and <= 65_536)
            && new[]
            {
                usage.OutputTokens,
                usage.ReasoningTokens,
            }.All(value => value is null or >= 0 and <= 8_192)
            && usage.CacheObservation is null or "cache.hit" or "cache.miss" or "cache.mixed"
                or "cache.not-reported"
            && expected.ObservationIds.Contains("usage-observed", StringComparer.Ordinal)
            && (usage.CacheObservation is null
                || expected.ObservationIds.Contains(usage.CacheObservation, StringComparer.Ordinal));
    }

    private static void ValidateSelection(EvaluationSelection selection)
    {
        if (selection.SchemaVersion != 1
            || !IsId(selection.SelectionId)
            || selection.Endpoint != "https://api.openai.com/v1/chat/completions"
            || selection.Model != "gpt-4.1-mini-2025-04-14"
            || !DateOnly.TryParseExact(
                selection.EvidenceDate,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _)
            || selection.Documentation.Length == 0
            || !selection.Documentation.SequenceEqual(
            [
                "https://developers.openai.com/api/docs/models/gpt-4.1-mini",
                "https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions",
            ], StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(selection.Reason)
            || selection.Reason.Length > 1_024
            || selection.ExpectedObservations.Length == 0
            || selection.ExpectedObservations.Any(observation => !IsId(observation))
            || !selection.ExpectedObservations.SequenceEqual(
                selection.ExpectedObservations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            || selection.LiveScenarioIds.Length == 0
            || selection.LiveScenarioIds.Any(id => !IsId(id))
            || !selection.LiveScenarioIds.SequenceEqual(
                selection.LiveScenarioIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            || selection.Limits.MaximumAttempts != 2
            || selection.Limits.MaximumContextReferences != 8
            || selection.Limits.MaximumContextUtf8Bytes != 65_536
            || selection.Limits.MaximumEvidenceReferences != 32
            || selection.Limits.MaximumEvidenceUtf8Bytes != 65_536
            || selection.Limits.MaximumProviderRequests != 8
            || selection.Limits.MaximumToolRounds != 4
            || selection.Limits.MaximumToolCalls != 16
            || selection.Limits.MaximumInputTokens != 65_536
            || selection.Limits.MaximumUncachedInputTokens != 32_768
            || selection.Limits.MaximumOutputTokens != 8_192
            || selection.Limits.MaximumCostMicrounits != 5_000_000
            || selection.Limits.MaximumElapsedMilliseconds != 120_000)
        {
            throw new InvalidDataException("evaluation.selection.invalid");
        }
    }

    private static string ResolveFile(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Join(root, relative));
        var check = Path.GetRelativePath(root, candidate);
        if (check is "." or ".."
            || Path.IsPathRooted(check)
            || check.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(candidate)
            || ContainsReparsePoint(root, candidate))
        {
            throw new InvalidDataException("evaluation.manifest.path-invalid");
        }

        return candidate;
    }

    private static void ValidateFileClosure(string root, EvaluationManifest manifest)
    {
        var expected = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Directory, bool Generated)>();
        pending.Push((new DirectoryInfo(root), false));
        while (pending.TryPop(out var item))
        {
            foreach (var entry in item.Directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("evaluation.manifest.path-invalid");
                }

                var relative = Path.GetRelativePath(root, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (entry is DirectoryInfo directory)
                {
                    pending.Push((
                        directory,
                        item.Generated || relative is "repository/bin" or "repository/obj"));
                }
                else if (!item.Generated && relative != "manifest.json")
                {
                    actual.Add(relative);
                }
            }
        }

        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("evaluation.manifest.file-set-mismatch");
        }
    }

    private static bool IsRelative(string value) =>
        !string.IsNullOrEmpty(value)
        && !Path.IsPathRooted(value)
        && !value.Contains('\\')
        && value.Split('/').All(segment => segment is not ("" or "." or ".."))
        && value.IndexOf('\0') < 0;

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        var current = root;
        foreach (var component in Path.GetRelativePath(root, candidate).Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsId(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 128
        && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is ('.' or '-' or '_'));

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool KnownScript(string value) => value is
        "tool-proposal" or "proposal" or "skip" or "invalid-tool" or "malformed-output"
        or "rate-limited" or "unavailable" or "budget-exhausted" or "timeout";

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
