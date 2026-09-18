using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ContractScribe.Cli;

// One physical configuration input admitted to an execution. Content is the
// exact byte sequence admitted at resolution; revalidation repeats the same
// regular-file, length, timestamp, and content checks used at admission, so
// any mutation makes the whole source fail closed.
internal sealed record CampaignConfigurationSource(
    string Path,
    long Length,
    DateTime LastWriteUtc,
    string Sha256,
    byte[] Content)
{
    internal bool Unchanged()
    {
        try
        {
            if (!CliPreflight.IsRegularFileNoFollow(Path))
            {
                return false;
            }
            var info = new FileInfo(Path);
            if (info.Length != Length || info.LastWriteTimeUtc != LastWriteUtc)
            {
                return false;
            }
            var bytes = File.ReadAllBytes(Path);
            return bytes.LongLength == Length
                && string.Equals(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Sha256,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (CliPreflight.IsPathFailure(exception))
        {
            return false;
        }
    }
}

// The complete resolved campaign execution configuration plus every physical
// input it was resolved from. Revalidate() is the single predicate used by the
// existing admission, in-session, dispatch-guard, and reconciler call sites;
// their stage-specific outcomes are unchanged.
internal sealed record CampaignResolvedConfigurationSnapshot(
    ImmutableArray<CampaignConfigurationSource> Sources,
    CampaignConfigurationDocument Document)
{
    internal bool Revalidate() => Sources.All(source => source.Unchanged());
}
