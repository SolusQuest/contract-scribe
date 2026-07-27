using System.Diagnostics;
using System.Security.Cryptography;
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
    public void HistoricalManifestRetainsItsOriginalM04InputsAndClosedProfile()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "m0.5-native-aot-manifest.json")));

        Assert.Equal("c728b8ab10696767de6a37809f4cde60bdb060621ce3febec1869b92b5801bd3", manifest.RootElement.GetProperty("m04ManifestSha256").GetString());
        Assert.Equal("63e7aa5c0cc16f10b1a5f732f69ca76379a0b34c", manifest.RootElement.GetProperty("m04FrozenSourceRevision").GetString());
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
    public void ReproductionSupportsSquashedHistoryAndAggregatePreservesMixedWarnings()
    {
        var root = FindRepositoryRoot();
        var verifier = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "verify-m0.5.ps1"));
        var aggregate = File.ReadAllText(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "aggregate-m0.5.ps1"));
        Assert.Contains("cat-file", verifier, StringComparison.Ordinal);
        Assert.Contains("m04FrozenSourceRevision", verifier, StringComparison.Ordinal);
        Assert.Contains("verify-m0.5-provenance.ps1", verifier, StringComparison.Ordinal);
        Assert.Contains("$aggregateOutcome -eq \"feasible-clean\"", aggregate, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("feasible-clean", "feasible-clean", "feasible-clean", 0)]
    [InlineData("feasible-clean", "not-feasible", "mixed", 0)]
    [InlineData("not-feasible", "not-feasible", "not-feasible", 0)]
    [InlineData("inconclusive", "feasible-clean", "inconclusive", 1)]
    public void AggregateScriptImplementsTheClosedTruthTable(string firstOutcome, string secondOutcome, string expectedOutcome, int expectedExitCode)
    {
        var root = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Join(root, "TestResults", "m05-aggregate-tests", runId);
        Directory.CreateDirectory(directory);
        var evidenceRoot = Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "evidence");
        var scratchDirectory = Path.Join(evidenceRoot, ".m05-aggregate-scratch-" + runId);
        var scratchSummaryPath = Path.Join(scratchDirectory, "m0.5-summary-v1.json");
        var scratchOutputArgument = "tests/fixtures/roslyn-msbuild/v1/evidence/.m05-aggregate-scratch-" + runId + "/m0.5-summary-v1.json";
        var trackedSummaryPath = Path.Join(evidenceRoot, "m0.5-summary-v1.json");
        var trackedStatusBefore = GitTrackedFixtureStatus(root);
        var trackedSummaryHashBefore = Sha256(trackedSummaryPath);
        try
        {
            var normalizedEvidenceRoot = Path.GetFullPath(evidenceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedScratch = Path.GetFullPath(scratchSummaryPath);
            Assert.True(normalizedScratch.StartsWith(normalizedEvidenceRoot, StringComparison.Ordinal), "The scratch summary path escapes the controlled evidence directory.");
            Assert.Equal("m0.5-summary-v1.json", Path.GetFileName(normalizedScratch));

            var linuxPath = Path.Join(directory, "linux.json");
            var windowsPath = Path.Join(directory, "windows.json");
            File.WriteAllText(linuxPath, CreateCell("Ubuntu", "linux-x64", firstOutcome));
            File.WriteAllText(windowsPath, CreateCell("Windows", "win-x64", secondOutcome));
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "aggregate-m0.5.ps1"));
            startInfo.ArgumentList.Add("-LinuxEvidencePath");
            startInfo.ArgumentList.Add(linuxPath);
            startInfo.ArgumentList.Add("-WindowsEvidencePath");
            startInfo.ArgumentList.Add(windowsPath);
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(scratchOutputArgument);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == expectedExitCode, $"Aggregate exit code mismatch. stdout: {stdout}; stderr: {stderr}");
            Assert.Contains($"M0.5 aggregate outcome: {expectedOutcome}", stdout, StringComparison.Ordinal);
            using var summary = JsonDocument.Parse(File.ReadAllText(scratchSummaryPath));
            Assert.Equal(expectedOutcome, summary.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(expectedExitCode, summary.RootElement.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            DeleteDirectoryWithRetries(scratchDirectory);
            DeleteDirectoryWithRetries(directory);
        }

        Assert.False(Directory.Exists(scratchDirectory), "The run's scratch directory was not cleaned up.");
        Assert.Equal(trackedStatusBefore, GitTrackedFixtureStatus(root));
        Assert.Equal(trackedSummaryHashBefore, Sha256(trackedSummaryPath));
    }

    private const string AggregateHelperScript = """
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)]
            [string]$AggregatePath,
            [Parameter(Mandatory = $true)]
            [string]$LinuxEvidencePath,
            [Parameter(Mandatory = $true)]
            [string]$WindowsEvidencePath,
            [Parameter(Mandatory = $true)]
            [string]$OutputPath,
            [Parameter(Mandatory = $true)]
            [string]$ScratchSummaryPath,
            [Parameter(Mandatory = $true)]
            [string]$ReadyPath,
            [Parameter(Mandatory = $true)]
            [string]$ReleasePath
        )
        $ErrorActionPreference = "Stop"
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = "pwsh"
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        [void]$startInfo.ArgumentList.Add("-NoProfile")
        [void]$startInfo.ArgumentList.Add("-File")
        [void]$startInfo.ArgumentList.Add($AggregatePath)
        [void]$startInfo.ArgumentList.Add("-LinuxEvidencePath")
        [void]$startInfo.ArgumentList.Add($LinuxEvidencePath)
        [void]$startInfo.ArgumentList.Add("-WindowsEvidencePath")
        [void]$startInfo.ArgumentList.Add($WindowsEvidencePath)
        [void]$startInfo.ArgumentList.Add("-OutputPath")
        [void]$startInfo.ArgumentList.Add($OutputPath)
        $nested = [Diagnostics.Process]::Start($startInfo)
        $nestedStdout = $nested.StandardOutput.ReadToEnd()
        $nestedStderr = $nested.StandardError.ReadToEnd()
        $nested.WaitForExit()
        Write-Output "nested aggregate pid=$($nested.Id) exit=$($nested.ExitCode)"
        Write-Output $nestedStdout
        if ($nestedStderr) { Write-Output "nested stderr: $nestedStderr" }
        if ($nested.ExitCode -ne 0) { exit 10 }
        if (-not (Test-Path -LiteralPath $ScratchSummaryPath)) { exit 11 }
        $summary = Get-Content -LiteralPath $ScratchSummaryPath -Raw | ConvertFrom-Json
        if ($summary.outcome -ne "feasible-clean" -or $summary.exitCode -ne 0) { exit 12 }
        [IO.File]::WriteAllText($ReadyPath, "ready`n", [Text.UTF8Encoding]::new($false))
        while (-not (Test-Path -LiteralPath $ReleasePath)) { Start-Sleep -Milliseconds 200 }
        exit 0
        """;

    [Fact]
    public void AggregateScriptInterruptionNeverMutatesTheTrackedSummary()
    {
        var root = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Join(root, "TestResults", "m05-aggregate-tests", runId);
        Directory.CreateDirectory(directory);
        var evidenceRoot = Path.Join(root, "tests", "fixtures", "roslyn-msbuild", "v1", "evidence");
        var scratchDirectory = Path.Join(evidenceRoot, ".m05-aggregate-scratch-" + runId);
        var scratchSummaryPath = Path.Join(scratchDirectory, "m0.5-summary-v1.json");
        var scratchOutputArgument = "tests/fixtures/roslyn-msbuild/v1/evidence/.m05-aggregate-scratch-" + runId + "/m0.5-summary-v1.json";
        var trackedSummaryPath = Path.Join(evidenceRoot, "m0.5-summary-v1.json");
        var trackedStatusBefore = GitTrackedFixtureStatus(root);
        var trackedSummaryHashBefore = Sha256(trackedSummaryPath);
        Process? helper = null;
        try
        {
            var linuxPath = Path.Join(directory, "linux.json");
            var windowsPath = Path.Join(directory, "windows.json");
            File.WriteAllText(linuxPath, CreateCell("Ubuntu", "linux-x64", "feasible-clean"));
            File.WriteAllText(windowsPath, CreateCell("Windows", "win-x64", "feasible-clean"));
            var readyPath = Path.Join(directory, "ready.signal");
            var releasePath = Path.Join(directory, "release.signal");
            var helperPath = Path.Join(directory, "aggregate-helper.ps1");
            File.WriteAllText(helperPath, AggregateHelperScript);

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(helperPath);
            startInfo.ArgumentList.Add("-AggregatePath");
            startInfo.ArgumentList.Add(Path.Join(root, "tests", "ContractScribe.Roslyn.NativeAot.Experiment", "aggregate-m0.5.ps1"));
            startInfo.ArgumentList.Add("-LinuxEvidencePath");
            startInfo.ArgumentList.Add(linuxPath);
            startInfo.ArgumentList.Add("-WindowsEvidencePath");
            startInfo.ArgumentList.Add(windowsPath);
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(scratchOutputArgument);
            startInfo.ArgumentList.Add("-ScratchSummaryPath");
            startInfo.ArgumentList.Add(scratchSummaryPath);
            startInfo.ArgumentList.Add("-ReadyPath");
            startInfo.ArgumentList.Add(readyPath);
            startInfo.ArgumentList.Add("-ReleasePath");
            startInfo.ArgumentList.Add(releasePath);
            helper = Process.Start(startInfo)!;

            var deadline = DateTime.UtcNow.AddSeconds(120);
            var ready = false;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(readyPath))
                {
                    ready = true;
                    break;
                }
                if (helper.HasExited)
                {
                    break;
                }
                Thread.Sleep(100);
            }

            if (!ready && !helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                var timeoutKillTerminated = helper.WaitForExit(30000);
                Assert.True(timeoutKillTerminated, "The helper process tree did not terminate after the readiness-timeout kill.");
            }

            if (!ready)
            {
                var helperState = helper.HasExited ? "premature exit code " + helper.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "still running after timeout";
                Assert.True(ready, $"The helper did not reach readiness ({helperState}). stdout: {helper.StandardOutput.ReadToEnd()}; stderr: {helper.StandardError.ReadToEnd()}");
            }

            helper.Kill(entireProcessTree: true);
            var terminated = helper.WaitForExit(30000);
            Assert.True(terminated, "The helper process tree did not terminate within the bounded wait after the deliberate kill.");

            Assert.Equal(trackedStatusBefore, GitTrackedFixtureStatus(root));
            Assert.Equal(trackedSummaryHashBefore, Sha256(trackedSummaryPath));
            var residue = Directory.EnumerateFileSystemEntries(evidenceRoot, ".m05-aggregate-scratch-*").ToArray();
            Assert.All(residue, entry => Assert.Equal(scratchDirectory, entry));
        }
        finally
        {
            if (helper is not null)
            {
                if (!helper.HasExited)
                {
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(30000);
                }
                helper.Dispose();
            }
            DeleteDirectoryWithRetries(scratchDirectory);
            DeleteDirectoryWithRetries(directory);
        }

        Assert.False(Directory.Exists(scratchDirectory), "The interrupted run's scratch residue was not cleaned up.");
    }

    private static string GitTrackedFixtureStatus(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain");
        startInfo.ArgumentList.Add("--untracked-files=no");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("tests/fixtures/roslyn-msbuild/v1");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git status failed. stderr: {stderr}");
        return stdout;
    }

    private static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void DeleteDirectoryWithRetries(string path)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException exception)
            {
                if (attempt == 4) Console.Error.WriteLine($"Cleanup failed for {path}: {exception.Message}");
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException exception)
            {
                if (attempt == 4) Console.Error.WriteLine($"Cleanup failed for {path}: {exception.Message}");
                Thread.Sleep(200);
            }
        }
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
