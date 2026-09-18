using System.Security.Cryptography;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed class CampaignPreflightException(string outcome) : Exception(outcome)
{
    internal string Outcome { get; } = outcome;
}

internal sealed record CampaignPreflightResult(
    CampaignOperation Operation,
    string RepositoryRoot,
    string InputPath,
    string InputIdentity,
    byte[] PolicyBytes,
    string SnapshotBinding,
    string StatePath,
    CampaignResolvedConfigurationSnapshot Configuration);

internal static class CampaignPreflight
{
    internal static CampaignPreflightResult Run(
        CampaignCommandArguments arguments,
        string currentDirectory) =>
        Run(arguments, currentDirectory, CliBuildIdentity.Current);

    internal static CampaignPreflightResult Run(
        CampaignCommandArguments arguments,
        string currentDirectory,
        CliBuildIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);
        try
        {
            var inputs = CliPreflight.ResolveInputs(
                arguments.RepositoryRoot,
                arguments.Input,
                arguments.Policy,
                currentDirectory);
            var state = ResolveState(arguments.State, currentDirectory, inputs.RepositoryRoot);
            var configuration = arguments.Kind switch
            {
                CampaignConfigurationKind.RuntimeAuthority =>
                    ReadConfiguration(arguments.Configuration, currentDirectory),
                _ => LayeredCampaignConfigurationResolver.Resolve(
                    arguments,
                    currentDirectory,
                    LayeredCampaignConfigurationResolver.PayloadDefaultsPath,
                    identity),
            };
            var inputIdentity = Path.GetRelativePath(inputs.RepositoryRoot, inputs.InputPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return new CampaignPreflightResult(
                arguments.Operation,
                inputs.RepositoryRoot,
                inputs.InputPath,
                inputIdentity,
                inputs.PolicyBytes,
                arguments.Snapshot,
                state,
                configuration);
        }
        catch (CampaignPreflightException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CampaignConfigurationException
            || CliPreflight.IsPathFailure(exception)
            || exception is CryptographicException)
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
    }

    private static string ResolveState(string value, string currentDirectory, string repositoryRoot)
    {
        var lexical = Path.GetFullPath(value, currentDirectory);
        var parent = Path.GetDirectoryName(lexical)
            ?? throw new CampaignPreflightException("campaign.invalid-configuration");
        var resolvedParent = CliPreflight.ResolveExistingPath(parent);
        if (!Directory.Exists(resolvedParent))
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
        var state = Path.Join(resolvedParent, Path.GetFileName(lexical));
        if (CliPreflight.IsContainedOrEqual(repositoryRoot, state))
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
        return state;
    }

    private static CampaignResolvedConfigurationSnapshot ReadConfiguration(
        string? value,
        string currentDirectory)
    {
        var lexical = Path.GetFullPath(
            value ?? throw new CampaignPreflightException("campaign.invalid-configuration"),
            currentDirectory);
        var path = CliPreflight.ResolveExistingPath(lexical);
        if (!CliPreflight.IsRegularFileNoFollow(path))
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
        var infoBefore = new FileInfo(path);
        if (infoBefore.Length is <= 0 or > 262_144)
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
        var bytes = File.ReadAllBytes(path);
        var infoAfter = new FileInfo(path);
        if (infoBefore.Length != bytes.LongLength
            || infoBefore.Length != infoAfter.Length
            || infoBefore.LastWriteTimeUtc != infoAfter.LastWriteTimeUtc)
        {
            throw new CampaignPreflightException("campaign.invalid-configuration");
        }
        var document = CampaignConfiguration.Parse(bytes);
        return new CampaignResolvedConfigurationSnapshot(
            [new CampaignConfigurationSource(
                path,
                bytes.LongLength,
                infoAfter.LastWriteTimeUtc,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes)],
            document);
    }
}
