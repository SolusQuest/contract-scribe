using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void EvidenceSchemaRejectsMixedRecordShapesAndContradictoryComparison()
    {
        var mixedNode = JsonNode.Parse(CreateCell("Windows", "win-x64", "feasible-clean"))!.AsObject();
        mixedNode["protocolFailure"] = new JsonObject { ["phase"] = "evidence", ["code"] = "evidence.contract-invalid" };
        using var mixed = JsonDocument.Parse(mixedNode.ToJsonString());
        Assert.False(EvidenceSchema.Value.Evaluate(mixed.RootElement).IsValid);

        var notRunNode = JsonNode.Parse(CreateCell("Windows", "win-x64", "inconclusive"))!.AsObject();
        notRunNode["comparison"]!["aotPayloadSha256"] = new string('a', 64);
        using var contradictoryComparison = JsonDocument.Parse(notRunNode.ToJsonString());
        Assert.False(EvidenceSchema.Value.Evaluate(contradictoryComparison.RootElement).IsValid);
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
        Assert.Contains("current-tree", verifier, StringComparison.Ordinal);
        Assert.Contains("M0.4 V2", verifier, StringComparison.Ordinal);
        Assert.Contains("exit 1", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void ReproductionSupportsSquashedHistoryAndAggregatePreservesMixedWarnings()
    {
        var root = FindRepositoryRoot();
        var verifier = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "reproduce-m0.5-v1.ps1"));
        var aggregate = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "reproduce-m0.5-v1-aggregate.ps1"));
        Assert.Contains("63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e", verifier, StringComparison.Ordinal);
        Assert.Contains("ed305a36f076d2d9aef981c44746d7a5a34d5bff", verifier, StringComparison.Ordinal);
        Assert.Contains("worktree", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transitive", aggregate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e", aggregate, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceFallbackAcceptsANonAncestorSquashedTreeAndRejectsUnexpectedFiles()
    {
        var root = FindRepositoryRoot();
        var testScript = Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "test-m0.5-provenance.ps1");
        var verifierScript = Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "verify-m0.5-provenance.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(testScript);
        startInfo.ArgumentList.Add("-VerifierPath");
        startInfo.ArgumentList.Add(verifierScript);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Provenance regression failed. stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("non-ancestral squash accepted", stdout, StringComparison.Ordinal);
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

    [Fact]
    public void ManifestUsesTheExactClosedPostImplementationSet()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "m0.5-native-aot-manifest.json")));
        var actual = manifest.RootElement.GetProperty("allowedPostImplementationFiles")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-linux-x64-evidence-v1.json",
            "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json",
            "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-win-x64-evidence-v1.json",
            "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json"
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CurrentAggregateScriptIsAClosedLegacyTombstone()
    {
        var root = FindRepositoryRoot();
        var aggregate = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "aggregate-m0.5.ps1"));
        Assert.Contains("current-tree", aggregate, StringComparison.Ordinal);
        Assert.Contains("reproduce-m0.5-v1-aggregate.ps1", aggregate, StringComparison.Ordinal);
        Assert.Contains("exit 1", aggregate, StringComparison.Ordinal);
    }

    private static string CreateCell(string runnerOs, string rid, string outcome)
    {
        var comparison = outcome == "inconclusive"
            ? new JsonObject { ["status"] = "not-run" }
            : new JsonObject { ["status"] = "compared", ["frameworkPayloadSha256"] = new string('a', 64), ["aotPayloadSha256"] = new string(outcome == "not-feasible" ? 'b' : 'a', 64), ["repeatedAotPayloadByteEqual"] = true, ["frameworkByteEqual"] = outcome != "not-feasible" };
        var cell = new JsonObject
        {
            ["evidenceVersion"] = "m0.5-native-aot-evidence-v1",
            ["recordType"] = "cell",
            ["cell"] = new JsonObject { ["runnerOs"] = runnerOs, ["rid"] = rid, ["processArchitecture"] = "X64" },
            ["profile"] = new JsonObject { ["targetFramework"] = "net10.0", ["configuration"] = "Release", ["publishAot"] = true, ["selfContained"] = true, ["publishTrimmed"] = true, ["runtimeIdentifier"] = rid },
            ["commands"] = new JsonArray { new JsonArray("dotnet", "publish") },
            ["warnings"] = new JsonArray(),
            ["toolchain"] = new JsonObject { ["sdkVersion"] = "10.0.102", ["runtimeVersion"] = "10.0.9", ["msbuildVersion"] = "unknown", ["nativeCompilerId"] = runnerOs == "Windows" ? "msvc" : "clang", ["nativeCompilerVersion"] = "unknown", ["linkerId"] = runnerOs == "Windows" ? "link" : "lld", ["linkerVersion"] = "unknown", ["runnerOs"] = runnerOs, ["rid"] = rid, ["processArchitecture"] = "X64" },
            ["dependencies"] = new JsonArray("global-json"),
            ["outcome"] = outcome,
            ["phase"] = outcome == "inconclusive" ? "preflight" : "comparison",
            ["cause"] = outcome == "inconclusive" ? "native-toolchain" : "semantic-contract",
            ["comparison"] = comparison
        };
        if (outcome == "inconclusive") cell["code"] = "preflight.native-toolchain-unavailable";
        if (outcome == "not-feasible") cell["code"] = "comparison.payload-mismatch";
        if (outcome == "feasible-with-warnings") cell["warnings"] = new JsonArray { new JsonObject { ["phase"] = "publish", ["cause"] = "aot-analysis", ["code"] = "warning.reviewed" } };
        return cell.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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
