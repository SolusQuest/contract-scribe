[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$EvidenceReproduction,
    [string]$M05ManifestPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$fixtureRoot = Join-Path $repositoryRoot "tests\fixtures\roslyn-msbuild\v1"
$manifestPath = Join-Path $fixtureRoot "transfer-manifest.json"
$hostPath = Join-Path $repositoryRoot "tests\ContractScribe.Roslyn.Experiment\bin\$Configuration\net10.0\ContractScribe.Roslyn.Experiment.dll"
$outputRoot = Join-Path $repositoryRoot "TestResults\m0.4-protocol"

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

Assert-Condition (Test-Path -LiteralPath $hostPath) "The built experiment host was not found."
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$baseAllowedPostSourceFiles = @(
    ".github/workflows/ci.yml",
    "Directory.Packages.props",
    "tests/ContractScribe.Roslyn.Experiment/verify-m0.4.ps1",
    "tests/ContractScribe.Roslyn.Experiment/test-m0.4-provenance.ps1",
    "tests/ContractScribe.Tests/M05NativeAotContractTests.cs",
    "tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json",
    "docs/20_architecture/decisions/0001-loader-and-distribution-boundary.md",
    "docs/20_architecture/experiments/m0.4-framework-dependent-loading.md",
    "global.json"
)
$expectedM05PostSourceFiles = @(
    "schemas/experiments/m0.5-native-aot-evidence-v1.schema.json",
    "docs/20_architecture/experiments/m0.5-native-aot-registry-v1.json",
    "docs/20_architecture/experiments/m0.5-native-aot-feasibility.md",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/ContractScribe.Roslyn.NativeAot.Experiment.csproj",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/Program.cs",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/verify-m0.5.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/verify-m0.5-provenance.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/test-m0.5-provenance.ps1",
    "tests/ContractScribe.Roslyn.NativeAot.Experiment/aggregate-m0.5.ps1",
    "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json",
    "tests/ContractScribe.Tests/M05NativeAotContractTests.cs"
)
$expectedM05PostImplementationFiles = @(
    "tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-linux-x64-evidence-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-win-x64-evidence-v1.json",
    "tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json"
)
$allowedPostSourceFiles = $baseAllowedPostSourceFiles
$allowedPostImplementationFiles = @()
if (-not [string]::IsNullOrWhiteSpace($M05ManifestPath)) {
    $resolvedM05ManifestPath = (Resolve-Path -LiteralPath $M05ManifestPath).Path
    $repositoryRootPrefix = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($resolvedM05ManifestPath.StartsWith($repositoryRootPrefix, [StringComparison]::OrdinalIgnoreCase)) "The M0.5 manifest must be inside the repository."
    Assert-Condition ([IO.Path]::GetFileName($resolvedM05ManifestPath) -eq "m0.5-native-aot-manifest.json") "The M0.5 manifest path is not the closed manifest."
    $m05Manifest = Get-Content -LiteralPath $resolvedM05ManifestPath -Raw | ConvertFrom-Json
    $declaredM05PostSourceFiles = @($m05Manifest.allowedPostSourceFiles | ForEach-Object { $_.ToString() })
    Assert-Condition ((($declaredM05PostSourceFiles | Sort-Object) -join "`n") -ceq (($expectedM05PostSourceFiles | Sort-Object) -join "`n")) "The M0.5 manifest does not declare the closed post-source file set."
    $declaredM05PostImplementationFiles = @($m05Manifest.allowedPostImplementationFiles | ForEach-Object { $_.ToString() })
    Assert-Condition ((($declaredM05PostImplementationFiles | Sort-Object) -join "`n") -ceq (($expectedM05PostImplementationFiles | Sort-Object) -join "`n")) "The M0.5 manifest does not declare the closed post-implementation file set."
    $m04ManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($m05Manifest.m04ManifestSha256 -eq $m04ManifestHash) "The M0.5 manifest does not bind the M0.4 transfer manifest."
    Assert-Condition ($m05Manifest.m04FrozenSourceRevision -eq $manifest.sourceRevision) "The M0.5 manifest does not bind the frozen M0.4 source revision."
    $allowedPostSourceFiles += $declaredM05PostSourceFiles
    $allowedPostImplementationFiles = $declaredM05PostImplementationFiles
}

foreach ($entry in $manifest.protocolInputSha256.PSObject.Properties) {
    $protocolPath = Join-Path $repositoryRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $protocolPath) "A protocol input is missing: $($entry.Name)."
    Assert-Condition ($entry.Value -match "^[0-9a-f]{64}$") "A protocol input hash is not a lowercase SHA-256: $($entry.Name)."
    $actualProtocolHash = (Get-FileHash -LiteralPath $protocolPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($actualProtocolHash -eq $entry.Value) "Protocol input hash does not match the transfer manifest: $($entry.Name)."
}

$packagesPath = Join-Path $repositoryRoot "Directory.Packages.props"
$packagesXml = [xml](Get-Content -LiteralPath $packagesPath -Raw)
$centralPackageNodes = @($packagesXml.Project.ItemGroup.PackageVersion)
foreach ($entry in $manifest.packages.PSObject.Properties) {
    $matchingNodes = @($centralPackageNodes | Where-Object { $_.Include -eq $entry.Name })
    Assert-Condition ($matchingNodes.Count -eq 1) "The central package declaration is missing or duplicated: $($entry.Name)."
    Assert-Condition ($matchingNodes[0].Version -eq $entry.Value) "The central package declaration does not match the transfer manifest: $($entry.Name)."
}

Assert-Condition ($manifest.observedEvidence.payloadSha256 -match "^[0-9a-f]{64}$") "The observed M0.4 payload hash is not a lowercase SHA-256."
$resolvedSdkVersion = (& dotnet --version).Trim()
Assert-Condition ($resolvedSdkVersion -match "^\d+\.\d+\.\d+$") "The dotnet SDK version could not be resolved."
$isRecordedWindowsEvidence = $EvidenceReproduction -and (
    $env:RUNNER_OS -eq "Windows" -or
    ([string]::IsNullOrWhiteSpace($env:RUNNER_OS) -and $IsWindows)
)
$currentRevision = (& git rev-parse HEAD).Trim()
Assert-Condition ($currentRevision -match "^[0-9a-f]{40}$") "The current source revision could not be resolved."
Assert-Condition ($manifest.sourceRevision -match "^[0-9a-f]{40}$") "The transfer manifest source revision is not exact."
$sourceRange = "$($manifest.sourceRevision)..$currentRevision"
$sourceFiles = @(git diff --name-only $sourceRange)
$sourceDiffExitCode = $LASTEXITCODE
Assert-Condition ($sourceDiffExitCode -eq 0) "The transfer manifest source revision could not be verified: $($manifest.sourceRevision)."
Assert-Condition (($sourceFiles | Where-Object { $_ -notin ($allowedPostSourceFiles + $allowedPostImplementationFiles) }).Count -eq 0) "The transfer manifest source revision does not cover the current semantic source path."

foreach ($entry in $manifest.fixtureContentSha256.PSObject.Properties) {
    $path = Join-Path $fixtureRoot $entry.Name
    Assert-Condition (Test-Path -LiteralPath $path) "Manifest fixture file is missing."
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($actual -eq $entry.Value) "Fixture content hash does not match the transfer manifest."
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null

$payloadPaths = @()
for ($run = 1; $run -le 2; $run++) {
    $runDirectory = Join-Path $outputRoot "run-$run"
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $stdoutPath = Join-Path $runDirectory "stdout.txt"
    $stderrPath = Join-Path $runDirectory "stderr.txt"
    & dotnet $hostPath (Join-Path $fixtureRoot $manifest.solution) $runDirectory 1> $stdoutPath 2> $stderrPath
    $exitCode = $LASTEXITCODE
    $resultPath = Join-Path $runDirectory "result.json"
    $resultSummary = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw } else { "no-result-envelope" }
    Assert-Condition ($exitCode -eq 0) "The framework-dependent experiment did not succeed: $resultSummary"

    $payloadPath = Join-Path $runDirectory $manifest.comparison.artifact
    Assert-Condition (Test-Path -LiteralPath $resultPath) "The experiment result envelope was not written."
    Assert-Condition (Test-Path -LiteralPath $payloadPath) "The semantic payload was not written."
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-Condition ($result.status -eq "succeeded") "The result envelope did not report success."
    Assert-Condition ($result.toolchain.sdkVersion -eq $resolvedSdkVersion) "The selected SDK does not match the SDK resolved for global.json: $($result.toolchain.sdkVersion) vs $resolvedSdkVersion."
    Assert-Condition ($result.toolchain.discoveryType -eq "DotNetSdk") "The selected toolchain was not discovered as a dotnet SDK: $($result.toolchain.discoveryType)."
    if ($isRecordedWindowsEvidence) {
        Assert-Condition ($result.toolchain.sdkVersion -eq $manifest.observedEvidence.toolchain.sdkVersion) "Observed SDK evidence does not match the run: $($result.toolchain.sdkVersion)."
        Assert-Condition ($result.toolchain.msbuildVersion -eq $manifest.observedEvidence.toolchain.msbuildVersion) "Observed MSBuild evidence does not match the run: $($result.toolchain.msbuildVersion)."
        Assert-Condition ($result.toolchain.runtimeVersion -eq $manifest.observedEvidence.toolchain.runtimeVersion) "Observed runtime evidence does not match the run: $($result.toolchain.runtimeVersion)."
        Assert-Condition ($result.toolchain.processArchitecture -eq $manifest.observedEvidence.toolchain.processArchitecture) "Observed process architecture evidence does not match the run: $($result.toolchain.processArchitecture)."
    }
    $payloadPaths += $payloadPath

    $payloadBytes = [System.IO.File]::ReadAllBytes($payloadPath)
    Assert-Condition ($payloadBytes.Length -gt 0) "The semantic payload is empty."
    Assert-Condition (-not ($payloadBytes[0] -eq 0xEF -and $payloadBytes[1] -eq 0xBB -and $payloadBytes[2] -eq 0xBF)) "The semantic payload has a BOM."
    Assert-Condition ($payloadBytes[$payloadBytes.Length - 1] -ne 0x0A -and $payloadBytes[$payloadBytes.Length - 1] -ne 0x0D) "The semantic payload has a trailing newline."
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($payloadHash -eq $manifest.observedEvidence.payloadSha256) "Observed payload evidence does not match the current manifest: $payloadHash."

    $publicOutput = (Get-Content -LiteralPath $stdoutPath -Raw) + (Get-Content -LiteralPath $stderrPath -Raw) + (Get-Content -LiteralPath $resultPath -Raw)
    Assert-Condition ($publicOutput -notmatch "(?i)([A-Z]:\\|/home/|/Users/|authorization|bearer\s|access[_-]?token|api[_-]?key)") "Public experiment output contains a machine path or credential-like value."
}

$first = [System.IO.File]::ReadAllBytes($payloadPaths[0])
$second = [System.IO.File]::ReadAllBytes($payloadPaths[1])
Assert-Condition ([System.Linq.Enumerable]::SequenceEqual($first, $second)) "Fresh-process semantic payloads are not byte-identical."

Write-Output "M0.4 protocol verified: fixture hashes, toolchain identity, two fresh-process runs, byte comparison, and public-safety scan passed."
