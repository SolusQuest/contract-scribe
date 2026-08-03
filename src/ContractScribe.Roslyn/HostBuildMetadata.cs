using System.Reflection;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed record HostValidationBuildMetadata(
    string SourceRevision,
    string SourceConfigurationId,
    string BuildSdkVersion)
{
    public HostBuildProvenance ToProvenance() => new(
        SourceRevision,
        SourceConfigurationId,
        BuildSdkVersion,
        HostContractResources.ContractBaselineSha256,
        HostContractResources.FailureRegistrySha256,
        HostContractResources.CalibratedBoundsSha256);
}

internal static class HostBuildMetadata
{
    public static HostValidationBuildMetadata? Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(attribute => attribute.Value).ToArray(),
                StringComparer.Ordinal);
        if (!values.TryGetValue("ContractScribeHostValidationSubject", out var markers))
        {
            return null;
        }
        if (markers.Length != 1 || markers[0] != "enabled")
        {
            throw new InvalidOperationException("Validation subject activation metadata is ambiguous.");
        }
        return new HostValidationBuildMetadata(
            RequireSingle(values, "ContractScribeSourceRevision"),
            RequireSingle(values, "ContractScribeSourceConfigurationId"),
            RequireSingle(values, "ContractScribeBuildSdkVersion"));
    }

    private static string RequireSingle(
        IReadOnlyDictionary<string, string?[]> values,
        string key)
    {
        if (!values.TryGetValue(key, out var candidates)
            || candidates.Length != 1
            || string.IsNullOrEmpty(candidates[0]))
        {
            throw new InvalidOperationException("Validation subject provenance metadata is incomplete.");
        }
        return candidates[0]!;
    }
}
