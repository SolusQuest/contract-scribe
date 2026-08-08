using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContractScribe.Core.Hosting;

public enum HostExecutionOutcome
{
    InvalidInput,
    EnvironmentUnavailable,
    LoadFailure,
    AuditError,
    PublicationFailure,
    Cancelled,
    Timeout,
    Succeeded,
}

public enum HostStage
{
    Input,
    Environment,
    SdkDiscovery,
    WorkspaceLoad,
    Classification,
    DocumentationObservation,
    PolicyEvidence,
    Audit,
    ResultValidation,
    Publication,
    Shutdown,
    Internal,
}

public enum HostTerminalState
{
    Open,
    CommittedNonSuccess,
    CommittedResult,
}

public enum HostArtifactState
{
    Absent,
    Invalidated,
    Staged,
    Published,
}

public enum HostToolchainSelectionState
{
    NotSelected,
    Selected,
}

public enum HostDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum HostEnforcementClass
{
    InternallyEnforceable,
    CallerOrOsEnforced,
    ObservableOnly,
    NotEnforceableSelectedTopology,
}

public static class HostVocabulary
{
    public static string GetId(HostExecutionOutcome value) => value switch
    {
        HostExecutionOutcome.InvalidInput => "invalid-input",
        HostExecutionOutcome.EnvironmentUnavailable => "environment-unavailable",
        HostExecutionOutcome.LoadFailure => "load-failure",
        HostExecutionOutcome.AuditError => "audit-error",
        HostExecutionOutcome.PublicationFailure => "publication-failure",
        HostExecutionOutcome.Cancelled => "cancelled",
        HostExecutionOutcome.Timeout => "timeout",
        HostExecutionOutcome.Succeeded => "succeeded",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(HostStage value) => value switch
    {
        HostStage.Input => "input",
        HostStage.Environment => "environment",
        HostStage.SdkDiscovery => "sdk-discovery",
        HostStage.WorkspaceLoad => "workspace-load",
        HostStage.Classification => "classification",
        HostStage.DocumentationObservation => "documentation-observation",
        HostStage.PolicyEvidence => "policy-evidence",
        HostStage.Audit => "audit",
        HostStage.ResultValidation => "result-validation",
        HostStage.Publication => "publication",
        HostStage.Shutdown => "shutdown",
        HostStage.Internal => "internal",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(HostEnforcementClass value) => value switch
    {
        HostEnforcementClass.InternallyEnforceable => "internally-enforceable",
        HostEnforcementClass.CallerOrOsEnforced => "caller-or-os-enforced",
        HostEnforcementClass.ObservableOnly => "observable-only",
        HostEnforcementClass.NotEnforceableSelectedTopology =>
            "not-enforceable-selected-topology",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public sealed record HostBuildProvenance
{
    private static readonly Regex RevisionPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public HostBuildProvenance(string sourceRevision)
    {
        Require(RevisionPattern.IsMatch(sourceRevision), nameof(sourceRevision));
        SourceRevision = sourceRevision;
    }

    public string SourceRevision { get; }

    private static void Require(bool condition, string parameter)
    {
        if (!condition)
        {
            throw new ArgumentException("The build provenance value is not canonical.", parameter);
        }
    }
}

public sealed record HostToolchainFact
{
    private HostToolchainFact(
        HostToolchainSelectionState selectionState,
        string? sdkVersion,
        string? runtimeVersion,
        string? msbuildVersion,
        string? architecture)
    {
        SelectionState = selectionState;
        SdkVersion = sdkVersion;
        RuntimeVersion = runtimeVersion;
        MsbuildVersion = msbuildVersion;
        Architecture = architecture;
    }

    public HostToolchainSelectionState SelectionState { get; }

    public string? SdkVersion { get; }

    public string? RuntimeVersion { get; }

    public string? MsbuildVersion { get; }

    public string? Architecture { get; }

    public static HostToolchainFact NotSelected { get; } =
        new(HostToolchainSelectionState.NotSelected, null, null, null, null);

    public static HostToolchainFact Selected(
        string sdkVersion,
        string runtimeVersion,
        string msbuildVersion,
        string architecture)
    {
        RequireSafeToken(sdkVersion, nameof(sdkVersion));
        RequireSafeToken(runtimeVersion, nameof(runtimeVersion));
        RequireSafeToken(msbuildVersion, nameof(msbuildVersion));
        RequireSafeToken(architecture, nameof(architecture));
        return new(
            HostToolchainSelectionState.Selected,
            sdkVersion,
            runtimeVersion,
            msbuildVersion,
            architecture);
    }

    private static void RequireSafeToken(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || value.Any(character => character < 0x20 || character > 0x7e))
        {
            throw new ArgumentException("Toolchain identities must be bounded safe tokens.", parameter);
        }
    }
}

public sealed record HostDiagnosticFact
{
    private static readonly Regex CodePattern = new(
        "^[a-z0-9]+(?:[.-][a-z0-9]+)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public HostDiagnosticFact(
        string code,
        HostStage stage,
        HostDiagnosticSeverity severity,
        string templateId,
        IEnumerable<string>? arguments = null,
        string? repositoryRelativePath = null)
    {
        if (!CodePattern.IsMatch(code) || code.Length > 96)
        {
            throw new ArgumentException("Diagnostic codes must be canonical and bounded.", nameof(code));
        }
        if (!CodePattern.IsMatch(templateId) || templateId.Length > 96)
        {
            throw new ArgumentException("Template identities must be canonical and bounded.", nameof(templateId));
        }

        var normalizedArguments = (arguments ?? [])
            .Select(NormalizeSafeArgument)
            .ToImmutableArray();
        if (normalizedArguments.Length > 8)
        {
            throw new ArgumentException("Diagnostics accept at most eight safe arguments.", nameof(arguments));
        }

        Code = code;
        Stage = stage;
        Severity = severity;
        TemplateId = templateId;
        Arguments = normalizedArguments;
        RepositoryRelativePath = repositoryRelativePath is null
            ? null
            : NormalizeRepositoryPath(repositoryRelativePath);
    }

    public string Code { get; }

    public HostStage Stage { get; }

    public HostDiagnosticSeverity Severity { get; }

    public string TemplateId { get; }

    public ImmutableArray<string> Arguments { get; }

    public string? RepositoryRelativePath { get; }

    private static string NormalizeSafeArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        if (normalized.Length > 128
            || normalized.Any(character => char.IsControl(character))
            || ContainsCredentialMarker(normalized)
            || LooksLikeAbsolutePath(normalized))
        {
            throw new ArgumentException("Diagnostic arguments must be bounded public-safe values.");
        }
        return normalized;
    }

    private static string NormalizeRepositoryPath(string path)
    {
        if (LooksLikeAbsolutePath(path) || path.Contains('\0'))
        {
            throw new ArgumentException("Diagnostic paths must be repository-relative.", nameof(path));
        }
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Diagnostic paths must be canonical.", nameof(path));
        }
        return string.Join('/', segments);
    }

    private static bool ContainsCredentialMarker(string value) =>
        new[]
        {
            "authorization",
            "bearer",
            "credential",
            "password",
            "passwd",
            "private-key",
            "private_key",
            "secret",
            "token",
            "api-key",
            "api_key",
            "apikey",
            "connection-string",
            "connection_string",
        }.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeAbsolutePath(string value)
    {
        if (Path.IsPathRooted(value)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return value.Length >= 3
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] is '\\' or '/';
    }
}

public static class HostDiagnosticEnvelope
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ImmutableArray<HostDiagnosticFact> Normalize(
        IEnumerable<HostDiagnosticFact> facts,
        int maximumCount,
        int maximumUtf8Bytes,
        HostDiagnosticFact? requiredFact = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (maximumCount < 1 || maximumUtf8Bytes < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var ordered = facts
            .Distinct(HostDiagnosticFactComparer.Instance)
            .OrderBy(item => HostVocabulary.GetId(item.Stage), StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.TemplateId, StringComparer.Ordinal)
            .ThenBy(item => item.RepositoryRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (requiredFact is not null
            && !ordered.Contains(requiredFact, HostDiagnosticFactComparer.Instance))
        {
            throw new ArgumentException("The required diagnostic must be present in the envelope.", nameof(requiredFact));
        }

        var result = ImmutableArray.CreateBuilder<HostDiagnosticFact>();
        if (requiredFact is not null)
        {
            result.Add(requiredFact);
            if (JsonSerializer.SerializeToUtf8Bytes(result, CanonicalOptions).Length
                > maximumUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumUtf8Bytes),
                    "The diagnostic byte cap cannot retain the required primary fact.");
            }
        }
        foreach (var fact in ordered)
        {
            if (requiredFact is not null
                && HostDiagnosticFactComparer.Instance.Equals(fact, requiredFact))
            {
                continue;
            }
            if (result.Count == maximumCount)
            {
                break;
            }
            result.Add(fact);
            if (JsonSerializer.SerializeToUtf8Bytes(result, CanonicalOptions).Length
                > maximumUtf8Bytes)
            {
                result.RemoveAt(result.Count - 1);
                break;
            }
        }
        return result
            .OrderBy(item => HostVocabulary.GetId(item.Stage), StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.TemplateId, StringComparer.Ordinal)
            .ThenBy(item => item.RepositoryRelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private sealed class HostDiagnosticFactComparer : IEqualityComparer<HostDiagnosticFact>
    {
        public static HostDiagnosticFactComparer Instance { get; } = new();

        public bool Equals(HostDiagnosticFact? left, HostDiagnosticFact? right) =>
            ReferenceEquals(left, right)
            || left is not null
            && right is not null
            && left.Code == right.Code
            && left.Stage == right.Stage
            && left.Severity == right.Severity
            && left.TemplateId == right.TemplateId
            && left.RepositoryRelativePath == right.RepositoryRelativePath
            && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

        public int GetHashCode(HostDiagnosticFact value)
        {
            var hash = new HashCode();
            hash.Add(value.Code, StringComparer.Ordinal);
            hash.Add(value.Stage);
            hash.Add(value.Severity);
            hash.Add(value.TemplateId, StringComparer.Ordinal);
            hash.Add(value.RepositoryRelativePath, StringComparer.Ordinal);
            foreach (var argument in value.Arguments)
            {
                hash.Add(argument, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }
    }
}

public sealed record HostOutputCommit
{
    public HostOutputCommit(
        HostArtifactState state,
        string? sha256,
        long byteCount)
    {
        if (byteCount < 0
            || (state == HostArtifactState.Published) != (sha256 is not null)
            || (sha256 is not null
                && (sha256.Length != 64
                    || sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))))
        {
            throw new ArgumentException("The output commit fact is inconsistent.");
        }
        State = state;
        Sha256 = sha256;
        ByteCount = byteCount;
    }

    public HostArtifactState State { get; }

    public string? Sha256 { get; }

    public long ByteCount { get; }
}

public sealed record HostMeasuredBound
{
    public HostMeasuredBound(
        string name,
        string unit,
        long measured,
        long threshold,
        HostEnforcementClass enforcementClass)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(unit)
            || measured < 0
            || threshold < 1
            || measured > threshold
                && enforcementClass != HostEnforcementClass.ObservableOnly)
        {
            throw new ArgumentException("The measured bound fact is inconsistent.");
        }
        Name = name;
        Unit = unit;
        Measured = measured;
        Threshold = threshold;
        EnforcementClass = enforcementClass;
    }

    public string Name { get; }

    public string Unit { get; }

    public long Measured { get; }

    public long Threshold { get; }

    public HostEnforcementClass EnforcementClass { get; }
}

public sealed record HostFailureRegistryEntry(
    string Code,
    HostStage Stage,
    HostExecutionOutcome ExecutionOutcome);

public sealed record CommittedCanonicalResult
{
    public CommittedCanonicalResult(
        ReadOnlySpan<byte> bytes,
        string sha256,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(toolchain);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(sha256, actualSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The committed canonical result digest does not match its bytes.",
                nameof(sha256));
        }
        Bytes = ImmutableArray.Create(bytes.ToArray());
        Sha256 = sha256;
        Provenance = provenance;
        Toolchain = toolchain;
    }

    public ImmutableArray<byte> Bytes { get; }

    public string Sha256 { get; }

    public HostBuildProvenance Provenance { get; }

    public HostToolchainFact Toolchain { get; }
}

public sealed record HostTerminalRecord(
    HostExecutionOutcome ExecutionOutcome,
    AuditOutcome? AuditOutcome,
    HostTerminalState TerminalState,
    HostFailureRegistryEntry? Failure,
    HostBuildProvenance Provenance,
    HostToolchainFact Toolchain,
    HostOutputCommit OutputCommit,
    ImmutableArray<HostDiagnosticFact> Diagnostics,
    ImmutableArray<HostMeasuredBound> MeasuredBounds,
    long AcceptedSequence);
