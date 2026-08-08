using System.Text.RegularExpressions;

namespace ContractScribe.Cli;

internal sealed record CliBuildIdentity(
    string ToolVersion,
    string SourceRevision,
    string CliContractBaseline)
{
    private static readonly Regex VersionPattern = new(
        "^.+\\+(?<revision>[0-9a-f]{40})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static CliBuildIdentity Current { get; } = Create(
        CommandLineApplication.ApplicationVersion);

    internal static CliBuildIdentity Create(string informationalVersion)
    {
        var match = VersionPattern.Match(informationalVersion);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "The CLI assembly informational version must contain an exact source revision.");
        }
        var revision = match.Groups["revision"].Value;
        return new CliBuildIdentity(informationalVersion, revision, revision);
    }
}
