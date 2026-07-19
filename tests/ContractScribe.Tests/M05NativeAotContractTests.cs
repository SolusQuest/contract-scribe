using System.Security.Cryptography;
using System.Text.Json;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class M05NativeAotContractTests
{
    private static readonly Lazy<JsonSchema> EvidenceSchema = new(() => JsonSchema.FromText(
        File.ReadAllText(Path.Join(FindRepositoryRoot(), "schemas", "experiments", "m0.5-native-aot-evidence-v1.schema.json"))));

    [Fact]
    public void EvidenceSchemaAcceptsAConclusiveCellAndProtocolFailureShape()
    {
        using var cell = JsonDocument.Parse("""
            {"evidenceVersion":"m0.5-native-aot-evidence-v1","recordType":"cell","cell":{"runnerOs":"Windows","rid":"win-x64","processArchitecture":"X64"},"profile":{"targetFramework":"net10.0","configuration":"Release","publishAot":true,"selfContained":true,"publishTrimmed":true,"runtimeIdentifier":"win-x64"},"commands":[["dotnet","publish"]],"warnings":[],"toolchain":{"sdkVersion":"10.0.102","runtimeVersion":"10.0.9","msbuildVersion":"unknown","nativeCompilerId":"msvc","nativeCompilerVersion":"unknown","linkerId":"link","linkerVersion":"unknown","runnerOs":"Windows","rid":"win-x64","processArchitecture":"X64"},"dependencies":["global-json"],"outcome":"inconclusive","phase":"preflight","cause":"native-toolchain","code":"preflight.native-toolchain-unavailable","comparison":{"status":"not-run"}}
            """);
        using var protocol = JsonDocument.Parse("""
            {"evidenceVersion":"m0.5-native-aot-evidence-v1","recordType":"protocol-failure","cell":{"runnerOs":"Ubuntu","rid":"linux-x64","processArchitecture":"X64"},"profile":{"targetFramework":"net10.0","configuration":"Release","publishAot":true,"selfContained":true,"publishTrimmed":true,"runtimeIdentifier":"linux-x64"},"commands":[["dotnet","publish"]],"warnings":[],"toolchain":{"sdkVersion":"10.0.102","runtimeVersion":"10.0.9","msbuildVersion":"unknown","nativeCompilerId":"clang","nativeCompilerVersion":"unknown","linkerId":"lld","linkerVersion":"unknown","runnerOs":"Ubuntu","rid":"linux-x64","processArchitecture":"X64"},"dependencies":["global-json"],"protocolFailure":{"phase":"evidence","code":"evidence.artifact-malformed"}}
            """);

        Assert.True(EvidenceSchema.Value.Evaluate(cell.RootElement).IsValid);
        Assert.True(EvidenceSchema.Value.Evaluate(protocol.RootElement).IsValid);
    }

    [Fact]
    public void ManifestBindsTheImmutableM04TransferManifestAndClosedInputs()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "m0.5-native-aot-manifest.json")));
        var transferManifestPath = Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "transfer-manifest.json");
        var transferHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(transferManifestPath))).ToLowerInvariant();

        Assert.Equal(transferHash, manifest.RootElement.GetProperty("m04ManifestSha256").GetString());
        Assert.Matches("^[0-9a-f]{40}$", manifest.RootElement.GetProperty("m04FrozenSourceRevision").GetString()!);
        var implementationRevision = manifest.RootElement.GetProperty("implementationRevision").GetString()!;
        Assert.Matches("^[0-9a-f]{40}$", implementationRevision);
        Assert.NotEqual(new string('0', 40), implementationRevision);
        Assert.Equal("net10.0", manifest.RootElement.GetProperty("publishProfile").GetProperty("targetFramework").GetString());
        Assert.True(manifest.RootElement.GetProperty("publishProfile").GetProperty("publishAot").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("publishProfile").GetProperty("selfContained").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("publishProfile").GetProperty("publishTrimmed").GetBoolean());
    }

    [Fact]
    public void NativeAotHostIsNotPartOfTheNormalSolution()
    {
        var root = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Join(root, "ContractScribe.slnx"));
        Assert.DoesNotContain("NativeAot", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceExtensionIsExplicitAndDoesNotUseRawExceptions()
    {
        var root = FindRepositoryRoot();
        var verifier = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.Experiment", "verify-m0.4.ps1"));
        Assert.Contains("M05ManifestPath", verifier, StringComparison.Ordinal);
        Assert.Contains("expectedM05PostSourceFiles", verifier, StringComparison.Ordinal);
        Assert.Contains("allowedPostImplementationFiles", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("$_.Exception", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryClosesCellAndProtocolCodes()
    {
        var root = FindRepositoryRoot();
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "docs", "20_architecture", "experiments", "m0.5-native-aot-registry-v1.json")));
        var cellCodes = registry.RootElement.GetProperty("cellCodes");
        Assert.True(cellCodes.TryGetProperty("comparison.payload-mismatch", out var mismatch));
        Assert.Equal("semantic-contract", mismatch.GetProperty("allowedCauses")[0].GetString());
        Assert.True(registry.RootElement.GetProperty("protocolFailureCodes").GetArrayLength() >= 8);
        Assert.Equal("always-inconclusive", registry.RootElement.GetProperty("rules").GetProperty("unknownCause").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
