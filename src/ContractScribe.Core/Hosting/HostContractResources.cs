using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ContractScribe.Core.Hosting;

public static class HostContractResources
{
    private const string Prefix = "ContractScribe.Hosting.";
    private static readonly Lazy<ResourceSnapshot> Snapshot = new(Load);

    public static ReadOnlyMemory<byte> FailureRegistryBytes => Snapshot.Value.FailureRegistry;

    public static ReadOnlyMemory<byte> CalibratedBoundsBytes => Snapshot.Value.CalibratedBounds;

    public static ReadOnlyMemory<byte> CalibrationEvidenceBytes => Snapshot.Value.CalibrationEvidence;

    public static ReadOnlyMemory<byte> ContractBaselineBytes => Snapshot.Value.ContractBaseline;

    public static string FailureRegistrySha256 => Snapshot.Value.FailureRegistrySha256;

    public static string CalibratedBoundsSha256 => Snapshot.Value.CalibratedBoundsSha256;

    public static string CalibrationEvidenceSha256 => Snapshot.Value.CalibrationEvidenceSha256;

    public static string ContractBaselineSha256 => Snapshot.Value.ContractBaselineSha256;

    public static ImmutableArray<HostFailureRegistryEntry> FailureRegistry =>
        Snapshot.Value.FailureRows;

    public static HostFailureRegistryEntry RequireFailure(string code)
    {
        var matches = FailureRegistry.Where(row => row.Code == code).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException("The production Host failure row is not unique.");
    }

    public static long RequireBound(string name)
    {
        using var document = JsonDocument.Parse(CalibratedBoundsBytes);
        var matches = document.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Where(entry => entry.GetProperty("name").GetString() == name)
            .ToArray();
        return matches.Length == 1
            ? matches[0].GetProperty("limit").GetInt64()
            : throw new InvalidOperationException("The production Host bound is not unique.");
    }

    private static ResourceSnapshot Load()
    {
        var failureRegistry = Read("host-failure-registry-v1.json");
        var bounds = Read("host-calibrated-bounds-v1.json");
        var evidence = Read("host-calibration-evidence-v1.json");
        var baseline = Read("contract-baseline-manifest-v1.json");
        using var parsed = JsonDocument.Parse(failureRegistry);
        var rows = parsed.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => new HostFailureRegistryEntry(
                entry.GetProperty("code").GetString()!,
                ParseStage(entry.GetProperty("stage").GetString()!),
                ParseOutcome(entry.GetProperty("executionOutcome").GetString()!)))
            .ToImmutableArray();
        if (rows.Length == 0
            || rows.Select(row => row.Code).Distinct(StringComparer.Ordinal).Count() != rows.Length)
        {
            throw new InvalidOperationException("The embedded Host failure registry is invalid.");
        }
        var evidenceSha256 = Sha256(evidence);
        using var parsedBounds = JsonDocument.Parse(bounds);
        var evidenceBindings = parsedBounds.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("calibrationEvidenceSha256").GetString())
            .ToArray();
        if (evidenceBindings.Length == 0
            || evidenceBindings.Any(binding =>
                !string.Equals(binding, evidenceSha256, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The embedded Host calibrated bounds are not bound to the embedded calibration evidence.");
        }

        return new ResourceSnapshot(
            failureRegistry,
            bounds,
            evidence,
            baseline,
            Sha256(failureRegistry),
            Sha256(bounds),
            evidenceSha256,
            Sha256(baseline),
            rows);
    }

    private static byte[] Read(string name)
    {
        using var stream = typeof(HostContractResources).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException("A required embedded Host contract is unavailable.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HostStage ParseStage(string value) => value switch
    {
        "input" => HostStage.Input,
        "environment" => HostStage.Environment,
        "sdk-discovery" => HostStage.SdkDiscovery,
        "workspace-load" => HostStage.WorkspaceLoad,
        "classification" => HostStage.Classification,
        "documentation-observation" => HostStage.DocumentationObservation,
        "policy-evidence" => HostStage.PolicyEvidence,
        "audit" => HostStage.Audit,
        "result-validation" => HostStage.ResultValidation,
        "publication" => HostStage.Publication,
        "shutdown" => HostStage.Shutdown,
        "internal" => HostStage.Internal,
        _ => throw new InvalidOperationException("The embedded Host stage is unknown."),
    };

    private static HostExecutionOutcome ParseOutcome(string value) => value switch
    {
        "invalid-input" => HostExecutionOutcome.InvalidInput,
        "environment-unavailable" => HostExecutionOutcome.EnvironmentUnavailable,
        "load-failure" => HostExecutionOutcome.LoadFailure,
        "audit-error" => HostExecutionOutcome.AuditError,
        "publication-failure" => HostExecutionOutcome.PublicationFailure,
        "cancelled" => HostExecutionOutcome.Cancelled,
        "timeout" => HostExecutionOutcome.Timeout,
        _ => throw new InvalidOperationException("The embedded Host execution outcome is unknown."),
    };

    private sealed record ResourceSnapshot(
        byte[] FailureRegistry,
        byte[] CalibratedBounds,
        byte[] CalibrationEvidence,
        byte[] ContractBaseline,
        string FailureRegistrySha256,
        string CalibratedBoundsSha256,
        string CalibrationEvidenceSha256,
        string ContractBaselineSha256,
        ImmutableArray<HostFailureRegistryEntry> FailureRows);
}
