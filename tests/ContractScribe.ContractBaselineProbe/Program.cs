using System.Security.Cryptography;
using System.Text.Json;
using ContractScribe.ContractBaselineProbe;
using Json.Schema;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ContractScribe.ContractBaselineProbe <replay-json-file>");
    return 2;
}

using var replay = JsonDocument.Parse(
    File.ReadAllBytes(args[0]),
    new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
var repositoryRoot = FindRepositoryRoot();
var auditSchema = JsonSchema.FromText(File.ReadAllText(Path.Join(repositoryRoot, "schemas", "audit-result", "v1.schema.json")));
var canonicalInputs = new List<byte[]>();
foreach (var logicalInput in replay.RootElement.GetProperty("logicalInputs").EnumerateArray())
{
    if (!auditSchema.Evaluate(logicalInput).IsValid)
    {
        Console.Error.WriteLine("Replay input failed the Audit Result v1 schema.");
        return 3;
    }
    AuditResultCanonicalizer.ValidateReplayDocument(logicalInput);
    canonicalInputs.Add(AuditResultCanonicalizer.Canonicalize(logicalInput));
}

if (canonicalInputs.Count < 2 || canonicalInputs.Skip(1).Any(candidate => !candidate.SequenceEqual(canonicalInputs[0])))
{
    Console.Error.WriteLine("Replay inputs did not canonicalize to identical Audit Result bytes.");
    return 4;
}

Console.WriteLine(Convert.ToHexString(SHA256.HashData(canonicalInputs[0])).ToLowerInvariant());
return 0;

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root not found.");
}
