using System.Text.Json;
using ContractScribe.Core;

// A pure call into the exact built product parser, not another campaign invocation.
byte[] bytes;
try
{
    if (args.Length != 1 || new FileInfo(args[0]).Length is <= 0 or > 524288)
        throw new IOException();
    bytes = File.ReadAllBytes(args[0]);
    if (bytes.Length is <= 0 or > 524288)
        throw new IOException();
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.WriteLine("{\"accepted\":false}");
    return 1;
}

var parsed = CampaignStateJson.Parse(bytes);
if (!parsed.IsValid || parsed.Artifact is not { } artifact)
{
    Console.WriteLine("{\"accepted\":false}");
    return 1;
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    accepted = true,
    sha256 = artifact.Sha256,
    revision = artifact.CheckpointRevision,
    lineage = artifact.State.CampaignLineage,
}));
return 0;
